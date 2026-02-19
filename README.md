# GetSteamStatus

GCP Cloud Function，查詢指定 Steam 使用者目前正在玩的遊戲。

## API

接受 `steamid`（Steam 64-bit ID）作為 query parameter，回傳該玩家目前遊玩的遊戲名稱（繁體中文）。

### 回應格式

```json
{ "GameName": "遊戲名稱" }
```

若玩家不在遊戲中，`GameName` 為空字串。

## 安全機制

- CORS Origin 白名單
- IP 速率限制
- 全域每日請求上限
- 重複請求快取
- API Key 透過環境變數注入，不寫在程式碼中

## 技術

- .NET 8 / C#
- GCP Cloud Functions Gen 2（Cloud Run）
- Steam Web API

## CI/CD

push 到 `main` 時自動執行：

1. `dotnet test` 跑單元測試
2. 透過 Workload Identity Federation 認證 GCP（無金鑰）
3. 部署 Cloud Function

## 本地開發

```bash
# 跑測試
dotnet test Tests/SendHttpRequest.Tests.csproj

# 本地執行（需設定 STEAM_API_KEY 環境變數）
dotnet run
```
