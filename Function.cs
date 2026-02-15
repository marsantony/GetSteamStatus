using Google.Cloud.Functions.Framework;
using Google.Cloud.Functions.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SendHttpRequest;

public class Startup : FunctionsStartup
{
    public override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services)
    {
        services.AddHttpClient<IHttpFunction, Function>();
    }
}

[FunctionsStartup(typeof(Startup))]
public class Function : IHttpFunction
{
    private readonly HttpClient _httpClient;

    // ── 設定 ──
    private const string AllowedOrigin = "https://marsantony.github.io";
    private const int PerIpPerMinuteLimit = 10;
    private const int GlobalDailyLimit = 500;
    private const int DuplicateWindowSeconds = 30;
    private static readonly Regex SteamIdPattern = new(@"^\d{17}$", RegexOptions.Compiled);

    // ── gameId → gameName 快取（in-memory，max instance = 1 所以安全）──
    private static readonly ConcurrentDictionary<string, string> _gameNameCache = new();

    // ── 速率限制（in-memory）──
    private static readonly ConcurrentDictionary<string, List<DateTime>> _ipRequests = new();
    private static int _globalDailyCount = 0;
    private static DateTime _globalDailyReset = DateTime.UtcNow.Date.AddDays(1);

    // ── 重複請求限制（per steamid）──
    private static readonly ConcurrentDictionary<string, (DateTime time, string response)> _duplicateCache = new();

    public Function(HttpClient httpClient) =>
        _httpClient = httpClient;

    public async Task HandleAsync(HttpContext context)
    {
        var origin = context.Request.Headers["Origin"].FirstOrDefault() ?? "";

        // Origin 不符就直接拒絕（擋掉其他網站 + 沒帶 Origin 的工具）
        if (origin != AllowedOrigin)
        {
            context.Response.StatusCode = 403;
            return;
        }

        context.Response.Headers["Access-Control-Allow-Origin"] = AllowedOrigin;

        // 處理 preflight
        if (context.Request.Method == "OPTIONS")
        {
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET";
            context.Response.Headers["Access-Control-Max-Age"] = "3600";
            context.Response.StatusCode = 204;
            return;
        }

        context.Response.ContentType = "application/json";

        // ── steamid 參數驗證 ──
        var steamId = context.Request.Query["steamid"].FirstOrDefault() ?? "";
        if (!SteamIdPattern.IsMatch(steamId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync(
                JsonConvert.SerializeObject(new { error = "缺少有效的 steamid 參數" }));
            return;
        }

        // ── 速率限制檢查 ──
        var rateLimitResult = CheckRateLimit(context);
        if (rateLimitResult != null)
        {
            context.Response.StatusCode = 429;
            await context.Response.WriteAsync(
                JsonConvert.SerializeObject(new { error = rateLimitResult }));
            return;
        }

        // ── 重複請求檢查：同一個 steamid N 秒內回傳快取結果 ──
        if (_duplicateCache.TryGetValue(steamId, out var cached) &&
            (DateTime.UtcNow - cached.time).TotalSeconds < DuplicateWindowSeconds)
        {
            await context.Response.WriteAsync(cached.response);
            return;
        }

        // ── 主邏輯 ──
        var result = new Dictionary<string, string> { ["GameName"] = "" };

        try
        {
            var steamApiKey = Environment.GetEnvironmentVariable("STEAM_API_KEY") ?? "";

            var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={steamApiKey}&format=json&steamids={steamId}";
            var jObjGameId = await GetUrlResponse(url);
            var tokenGameId = jObjGameId.SelectToken("response.players[0].gameid");

            if (tokenGameId == null)
            {
                var response = JsonConvert.SerializeObject(result);
                UpdateDuplicateCache(steamId, response);
                await context.Response.WriteAsync(response);
                return;
            }

            var gameId = tokenGameId.Value<string>()!;

            // gameId → gameName 快取：同一個 gameId 不重複查
            if (_gameNameCache.TryGetValue(gameId, out var cachedName))
            {
                result["GameName"] = cachedName;
            }
            else
            {
                url = $"https://store.steampowered.com/api/appdetails?appids={gameId}&l=zh-tw";
                var jObjGameName = await GetUrlResponse(url);
                var tokenGameName = jObjGameName.SelectToken($"{gameId}.data.name");

                var gameName = tokenGameName?.Value<string>();
                if (!string.IsNullOrEmpty(gameName))
                {
                    result["GameName"] = gameName;
                    _gameNameCache[gameId] = gameName;
                }
            }
        }
        catch
        {
            // Steam API 異常時回傳空 GameName
        }

        var responseJson = JsonConvert.SerializeObject(result);
        UpdateDuplicateCache(steamId, responseJson);
        await context.Response.WriteAsync(responseJson);
    }

    private string? CheckRateLimit(HttpContext context)
    {
        var now = DateTime.UtcNow;

        // 每日全域限制重置
        if (now >= _globalDailyReset)
        {
            _globalDailyCount = 0;
            _globalDailyReset = now.Date.AddDays(1);
            _ipRequests.Clear();
        }

        // 全域每日限制
        if (_globalDailyCount >= GlobalDailyLimit)
        {
            return "已達每日請求上限";
        }
        _globalDailyCount++;

        // 每 IP 每分鐘限制
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var requests = _ipRequests.GetOrAdd(ip, _ => new List<DateTime>());

        lock (requests)
        {
            var oneMinuteAgo = now.AddMinutes(-1);
            requests.RemoveAll(t => t < oneMinuteAgo);

            if (requests.Count >= PerIpPerMinuteLimit)
            {
                return "請求過於頻繁，請稍後再試";
            }
            requests.Add(now);
        }

        return null;
    }

    internal static void ResetDuplicateCache()
    {
        _duplicateCache.Clear();
    }

    internal static void ResetState()
    {
        _gameNameCache.Clear();
        _ipRequests.Clear();
        _globalDailyCount = 0;
        _globalDailyReset = DateTime.UtcNow.Date.AddDays(1);
        _duplicateCache.Clear();
    }

    private static void UpdateDuplicateCache(string steamId, string response)
    {
        _duplicateCache[steamId] = (DateTime.UtcNow, response);
    }

    private async Task<JObject> GetUrlResponse(string url)
    {
        _httpClient.DefaultRequestHeaders.Remove("Accept-Language");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "zh-TW");

        using var clientResponse = await _httpClient.GetAsync(url);
        var content = await clientResponse.Content.ReadAsStringAsync();
        return JObject.Parse(content);
    }
}
