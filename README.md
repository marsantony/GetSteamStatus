# GetSteamStatus

GCP Cloud Function，查詢指定 Steam 使用者目前正在玩的遊戲。

## API

```
GET https://asia-east1-steamwebapi-394409.cloudfunctions.net/GetSteamStatus?steamid={steamid}
```

### 參數

| 參數 | 說明 |
|------|------|
| `steamid` | Steam 64-bit ID（17 位數字） |

### 回應

```json
{ "GameName": "遊戲名稱" }
```

遊戲名稱為繁體中文（`zh-TW`）。若玩家不在遊戲中，`GameName` 為空字串。

### 錯誤

| 狀態碼 | 說明 |
|--------|------|
| 400 | 缺少或無效的 `steamid` |
| 403 | Origin 不在白名單中 |
| 429 | 超過速率限制 |

## 安全機制

- **CORS Origin 白名單** — 僅允許 `marsantony.github.io`
- **IP 速率限制** — 每 IP 每分鐘 10 次
- **全域每日上限** — 500 次/天
- **重複請求快取** — 同一 steamid 30 秒內回傳快取結果
- **API Key 環境變數** — 不寫在程式碼中

## 技術

- .NET 8 / C#
- GCP Cloud Functions Gen 2（Cloud Run）
- Steam Web API

## CI/CD

push 到 `main` 時自動執行：

1. `dotnet test` 跑單元測試（15 個）
2. 透過 Workload Identity Federation 認證 GCP（無金鑰）
3. `gcloud functions deploy` 部署到 `asia-east1`

## 本地開發

```bash
# 跑測試
dotnet test Tests/SendHttpRequest.Tests.csproj

# 本地執行（需設定 STEAM_API_KEY 環境變數）
dotnet run
```
