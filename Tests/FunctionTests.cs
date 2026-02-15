using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using SendHttpRequest;
using System.Net;
using System.Net.Http;
using Xunit;

namespace SendHttpRequest.Tests;

public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses = new();
    public List<string> RequestedUrls { get; } = new();

    public void SetResponse(string urlContains, string jsonResponse)
    {
        _responses[urlContains] = jsonResponse;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        RequestedUrls.Add(url);

        foreach (var (key, value) in _responses)
        {
            if (url.Contains(key))
            {
                return Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(value)
                });
            }
        }

        return Task.FromResult(new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.InternalServerError,
            Content = new StringContent("error")
        });
    }
}

public class FunctionTests : IDisposable
{
    private const string ValidOrigin = "https://marsantony.github.io";
    private const string TestSteamId = "76561198003344359";

    // Steam API 模擬回應：玩遊戲中
    private const string PlayerPlayingResponse = """
        {"response":{"players":[{"steamid":"76561198003344359","gameid":"1245620"}]}}
        """;

    // Steam API 模擬回應：沒在玩遊戲
    private const string PlayerNotPlayingResponse = """
        {"response":{"players":[{"steamid":"76561198003344359"}]}}
        """;

    // Steam Store 模擬回應：遊戲名稱
    private const string AppDetailsResponse = """
        {"1245620":{"success":true,"data":{"name":"ELDEN RING"}}}
        """;

    public FunctionTests()
    {
        Function.ResetState();
        Environment.SetEnvironmentVariable("STEAM_API_KEY", "test-key");
    }

    public void Dispose()
    {
        Function.ResetState();
    }

    private static DefaultHttpContext CreateContext(
        string origin = ValidOrigin, string method = "GET",
        string? ip = null, string? steamid = TestSteamId)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Origin"] = origin;
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip ?? "127.0.0.1");
        if (steamid != null)
        {
            context.Request.QueryString = new QueryString($"?steamid={steamid}");
        }
        return context;
    }

    private static async Task<string> GetResponseBody(DefaultHttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private static Function CreateFunction(MockHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new Function(httpClient);
    }

    // ── Origin 檢查 ──

    [Fact]
    public async Task Origin不符_回傳403()
    {
        var handler = new MockHttpMessageHandler();
        var function = CreateFunction(handler);
        var context = CreateContext(origin: "https://evil-site.com");

        await function.HandleAsync(context);

        Assert.Equal(403, context.Response.StatusCode);
        Assert.Empty(handler.RequestedUrls);
    }

    [Fact]
    public async Task 沒帶Origin_回傳403()
    {
        var handler = new MockHttpMessageHandler();
        var function = CreateFunction(handler);
        var context = CreateContext(origin: "");

        await function.HandleAsync(context);

        Assert.Equal(403, context.Response.StatusCode);
    }

    // ── CORS Preflight ──

    [Fact]
    public async Task OPTIONS_preflight_回傳204()
    {
        var handler = new MockHttpMessageHandler();
        var function = CreateFunction(handler);
        var context = CreateContext(method: "OPTIONS");

        await function.HandleAsync(context);

        Assert.Equal(204, context.Response.StatusCode);
        Assert.Equal(ValidOrigin, context.Response.Headers["Access-Control-Allow-Origin"]);
        Assert.Equal("GET", context.Response.Headers["Access-Control-Allow-Methods"]);
    }

    // ── steamid 參數驗證 ──

    [Fact]
    public async Task 沒帶steamid_回傳400()
    {
        var handler = new MockHttpMessageHandler();
        var function = CreateFunction(handler);
        var context = CreateContext(steamid: null);

        await function.HandleAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        var body = await GetResponseBody(context);
        Assert.Contains("steamid", body);
    }

    [Fact]
    public async Task steamid格式不正確_回傳400()
    {
        var handler = new MockHttpMessageHandler();
        var function = CreateFunction(handler);
        var context = CreateContext(steamid: "not-a-steamid");

        await function.HandleAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
    }

    // ── 正常請求 ──

    [Fact]
    public async Task 玩遊戲中_回傳遊戲名稱()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse("GetPlayerSummaries", PlayerPlayingResponse);
        handler.SetResponse("appdetails", AppDetailsResponse);
        var function = CreateFunction(handler);
        var context = CreateContext();

        await function.HandleAsync(context);

        var body = await GetResponseBody(context);
        var json = JObject.Parse(body);
        Assert.Equal("ELDEN RING", json["GameName"]!.ToString());
    }

    [Fact]
    public async Task 沒在玩遊戲_回傳空GameName()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse("GetPlayerSummaries", PlayerNotPlayingResponse);
        var function = CreateFunction(handler);
        var context = CreateContext();

        await function.HandleAsync(context);

        var body = await GetResponseBody(context);
        var json = JObject.Parse(body);
        Assert.Equal("", json["GameName"]!.ToString());
    }

    [Fact]
    public async Task SteamAPI異常_回傳空GameName()
    {
        var handler = new MockHttpMessageHandler(); // 沒設定任何回應，預設回 500
        var function = CreateFunction(handler);
        var context = CreateContext();

        await function.HandleAsync(context);

        var body = await GetResponseBody(context);
        var json = JObject.Parse(body);
        Assert.Equal("", json["GameName"]!.ToString());
    }

    [Fact]
    public async Task API呼叫帶入正確steamid()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse("GetPlayerSummaries", PlayerNotPlayingResponse);
        var function = CreateFunction(handler);
        var context = CreateContext(steamid: "76561197999639100");

        await function.HandleAsync(context);

        Assert.Single(handler.RequestedUrls);
        Assert.Contains("76561197999639100", handler.RequestedUrls[0]);
    }

    // ── gameId 快取 ──

    [Fact]
    public async Task 同一個gameId_第二次不呼叫appdetails()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse("GetPlayerSummaries", PlayerPlayingResponse);
        handler.SetResponse("appdetails", AppDetailsResponse);
        var function = CreateFunction(handler);

        // 第一次請求：呼叫兩個 API
        var ctx1 = CreateContext();
        await function.HandleAsync(ctx1);
        Assert.Equal(2, handler.RequestedUrls.Count);

        // 只清重複請求快取，保留 gameId 快取
        Function.ResetDuplicateCache();
        handler.RequestedUrls.Clear();

        // 第二次請求：gameId 沒變，應該只呼叫 GetPlayerSummaries
        var ctx2 = CreateContext();
        await function.HandleAsync(ctx2);

        var body = await GetResponseBody(ctx2);
        var json = JObject.Parse(body);
        Assert.Equal("ELDEN RING", json["GameName"]!.ToString());
        Assert.Single(handler.RequestedUrls);
        Assert.Contains("GetPlayerSummaries", handler.RequestedUrls[0]);
    }

    // ── 重複請求限制 ──

    [Fact]
    public async Task 重複請求30秒內_回傳快取結果()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse("GetPlayerSummaries", PlayerPlayingResponse);
        handler.SetResponse("appdetails", AppDetailsResponse);
        var function = CreateFunction(handler);

        // 第一次請求
        var ctx1 = CreateContext();
        await function.HandleAsync(ctx1);
        var urlCountAfterFirst = handler.RequestedUrls.Count;

        // 第二次請求（30 秒內，同一個 steamid）
        var ctx2 = CreateContext();
        await function.HandleAsync(ctx2);

        // 第二次不應該有新的外部 API 呼叫
        Assert.Equal(urlCountAfterFirst, handler.RequestedUrls.Count);

        var body = await GetResponseBody(ctx2);
        var json = JObject.Parse(body);
        Assert.Equal("ELDEN RING", json["GameName"]!.ToString());
    }

    [Fact]
    public async Task 不同steamid_不共用重複請求快取()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse("GetPlayerSummaries", PlayerNotPlayingResponse);
        var function = CreateFunction(handler);

        // steamid A 請求
        var ctx1 = CreateContext(steamid: "76561198003344359");
        await function.HandleAsync(ctx1);
        var urlCountAfterFirst = handler.RequestedUrls.Count;

        // steamid B 請求（不同人，不應該命中快取）
        var ctx2 = CreateContext(steamid: "76561197999639100");
        await function.HandleAsync(ctx2);

        Assert.True(handler.RequestedUrls.Count > urlCountAfterFirst);
    }

    // ── 速率限制 ──

    [Fact]
    public async Task 超過每IP每分鐘限制_回傳429()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse("GetPlayerSummaries", PlayerNotPlayingResponse);
        var function = CreateFunction(handler);

        for (int i = 0; i < 10; i++)
        {
            var ctx = CreateContext(ip: "192.168.1.1");
            await function.HandleAsync(ctx);
        }

        // 第 11 次應該被擋
        var blockedCtx = CreateContext(ip: "192.168.1.1");
        await function.HandleAsync(blockedCtx);

        Assert.Equal(429, blockedCtx.Response.StatusCode);
        var body = await GetResponseBody(blockedCtx);
        Assert.Contains("請求過於頻繁", body);
    }

    [Fact]
    public async Task 不同IP不受彼此影響()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse("GetPlayerSummaries", PlayerNotPlayingResponse);
        var function = CreateFunction(handler);

        // IP-A 發送 10 次
        for (int i = 0; i < 10; i++)
        {
            var ctx = CreateContext(ip: "10.0.0.1");
            await function.HandleAsync(ctx);
        }

        // IP-B 第 1 次應該正常
        var ctxB = CreateContext(ip: "10.0.0.2");
        await function.HandleAsync(ctxB);

        Assert.Equal(200, ctxB.Response.StatusCode);
    }

    [Fact]
    public async Task 超過全域每日限制_回傳429()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse("GetPlayerSummaries", PlayerNotPlayingResponse);
        var function = CreateFunction(handler);

        // 用不同 IP 發送 500 次（避免觸發 per-IP 限制）
        for (int i = 0; i < 500; i++)
        {
            var ctx = CreateContext(ip: $"10.{i / 256}.{i % 256}.1");
            await function.HandleAsync(ctx);
        }

        // 第 501 次應該被擋
        var blockedCtx = CreateContext(ip: "10.99.99.99");
        await function.HandleAsync(blockedCtx);

        Assert.Equal(429, blockedCtx.Response.StatusCode);
        var body = await GetResponseBody(blockedCtx);
        Assert.Contains("每日請求上限", body);
    }
}
