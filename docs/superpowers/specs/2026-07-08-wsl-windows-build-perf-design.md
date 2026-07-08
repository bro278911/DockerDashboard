# WSL 與 Windows 兩軌 Docker 重建效能優化設計

日期：2026-07-08
狀態：已與使用者確認設計，待實作規劃

## 背景與問題

使用者核心需求：**修改檔案後，單服務「重新載入／重建 image／重啟」要快**，尤其 WSL 模式。

現況功能已齊（不需新增基本能力）：

- 手動「重建重啟」：`docker compose up -d --build --no-deps <服務>`（`RebuildRestartServiceAsync`）
- Auto Watch：`WatchRebuildService` 用 `FileSystemWatcher` 監看服務目錄，2 秒 debounce 後自動觸發上述重建重啟，per-service toggle 持久化於 settings

真正的落差是**速度**與**未來相容性**：

1. WSL2 模式下 build context 走 `/mnt/d`（9P 協定），實測 CMPBackend 共 15,190 檔 / 3.5 GB（其中 bin/obj/.git/node_modules 佔 13,604 檔）。CMP root `.dockerignore` 含負向樣式（`!**/.gitignore`、`!.git/*`），BuildKit 無法整棵剪枝，每次 build 都走訪全部檔案，固定成本 10~30 秒。
2. 專案若搬進 WSL ext4，本工具兩處會壞：
   - `ConvertToWslPath` 只認磁碟路徑（`X:\...`），`\\wsl$\...` UNC 不轉換，`wsl --cd` 失敗
   - `FileSystemWatcher` 對 `\\wsl$` 收不到變更通知（9P 不轉發 inotify），Auto Watch 失效

## 已確認的決策

| 決策點 | 結論 |
|---|---|
| 程式碼位置 | **兩軌都支援**：留 D:\（Windows 軌）與搬 WSL ext4（WSL 軌） |
| Docker 引擎 | 使用者兩者都有（Docker Desktop＋distro 內 docker-ce） |
| WSL 軌 Auto Watch 機制 | **`docker compose watch`** 常駐子程序（Linux 側原生 inotify） |
| 編輯器 | VS Code（WSL 軌用 Remote-WSL） |

## 目標使用組合矩陣

| | 程式碼在 D:\（現況） | 程式碼在 WSL ext4 |
|---|---|---|
| 建議 Docker 模式 | Docker Desktop 模式（native NTFS 掃 context，避開 9P） | WSL2 模式＋Desktop WSL Integration（全原生） |
| Auto Watch 機制 | 現有 `FileSystemWatcher`（不變） | `docker compose watch` 常駐程序 |
| 單檔 sync 不重建 | 無（config 變更也走重建） | compose watch `sync+restart`（appsettings 類秒級生效） |
| 預期單服務重建 | 掃描成本大減（native＋dockerignore 修正） | 掃描毫秒級，只剩真正 build 時間 |

## 範圍切分

三塊，只有第一塊在本 repo 實作；後兩塊為配套，寫成 checklist 供使用者（或另開 session 到 CMP repo）執行。

### A. 本工具改動（本 repo，3 項）

#### A1. `\\wsl$` / `\\wsl.localhost` UNC 路徑支援

- `ConvertToWslPath` 新增 UNC 解析：`\\wsl$\<distro>\home\x` 與 `\\wsl.localhost\<distro>\home\x` → `/home/x`
- distro 名不比對大小寫；UNC 中的 distro 與設定的 `WslDistroName` 不一致時照樣轉換（`wsl --cd` 用的是設定的 distro，路徑仍有效與否交給 wsl 回報錯誤）
- 不認得的 UNC（非 `wsl$` / `wsl.localhost` 前綴）原樣回傳，行為同現況
- 掃描器、快取（mtime stamps）、GitService 沿用 Windows 側 UNC 存取：`Directory.Exists`、`File.GetLastWriteTimeUtc`、`git.exe` 對 `\\wsl$` 都能運作（稍慢但有掃描快取，可接受）

#### A2. `COMPOSE_BAKE=true`

- build 類指令（重建重啟、增量重建、完整重建）設定環境變數 `COMPOSE_BAKE=true`，兩種模式都設（沿用既有 `withBuildEnv` 機制：Docker Desktop 模式設在 psi 環境變數，WSL2 模式經 `env` 前綴傳入）

#### A3. Watch 雙機制

- 專案路徑為 `\\wsl$` / `\\wsl.localhost` 開頭 → watch toggle 改為啟動/停止 `docker compose watch --no-up <服務>` 常駐子程序：
  - 每個 compose 檔一支程序，服務集合變動時重啟程序（帶新的服務清單參數）
  - stdout/stderr 串進日誌面板
  - 程序異常退出：記日誌，該 compose 檔下所有 watch toggle 自動關閉
  - 工具關閉：隨 `MainViewModel.Dispose` 終止子程序（kill process tree）
  - compose 檔缺 `develop.watch` 區段時 compose watch 會自行報錯退出 → 日誌顯示錯誤並關閉 toggle，不另做偵測
- Windows 磁碟路徑 → 維持現有 `FileSystemWatcher` 機制，零改動

### B. CMP repo 配套（另開 session 到該專案，走其流程）

1. **`.dockerignore` 刪負向行**：先 `grep -r "\.git" */Dockerfile` 確認 13 個 Dockerfile 沒讀 `.git/HEAD` 做版本戳記，確認後刪除 `!**/.gitignore`、`!.git/HEAD`、`!.git/config`、`!.git/packed-refs`、`!.git/refs/heads/**` 五行。效果：context 掃描 15,190 檔 → 約 1,600 檔，兩軌都受益。
2. **13 個 Dockerfile 加 NuGet cache mount**：`RUN --mount=type=cache,target=/root/.nuget/packages dotnet restore ...`。重建時 restore 由分鐘級降到秒級。
3. **compose 加 `develop.watch` 區段**（WSL 軌 Auto Watch 前提），每服務兩條規則：
   - `path: <服務目錄>`，`action: rebuild`（C# 變更自動重建重啟）
   - `path: <服務目錄>/appsettings*.json`，`action: sync+restart`，`target: /app`（config 單檔同步＋重啟容器，不重建）

### C. 系統設定（一次性手動步驟 checklist）

1. **Windows Defender 排除**：`D:\桌面內容\專案\CMP`、`wsl.exe`、`docker.exe`、Docker Desktop vhdx 目錄（`%LOCALAPPDATA%\Docker`）
2. **Docker Desktop → Settings → Resources → WSL Integration** 勾選使用者的 distro
3. **WSL 軌啟用時**：
   - WSL 內 `mkdir -p ~/projects && cd ~/projects && git clone <CMPBackend remote>`
   - VS Code 裝「WSL」extension，WSL terminal 內 `code .` 開啟（editor server 跑 Linux 側）
   - WSL 內裝 dotnet SDK（本機跑測試用）
   - 本工具以 `\\wsl$\<distro>\home\<user>\projects\CMPBackend` 匯入專案

## 錯誤處理

- compose watch 程序異常退出：日誌記錄退出碼與最後輸出，相關 watch toggle 自動關閉（UI 狀態與實際一致）
- UNC 轉換失敗（不認得的格式）：原樣傳遞，等同現況行為
- WSL 軌專案但 Docker 模式是 Docker Desktop：compose watch 照樣可跑（Windows 側 docker.exe 也支援 watch），但效能不佳 — 不做強制，文件註明建議組合

## 測試與驗收

- **單元測試**：`ConvertToWslPath` — 磁碟路徑（現況不回歸）、`\\wsl$\Ubuntu\home\x`、`\\wsl.localhost\Ubuntu-22.04\home\x`、大小寫、尾斜線、非 wsl UNC（`\\server\share`）原樣回傳
- **手動冒煙（WPF UI）**：
  - WSL 軌：`\\wsl$` 匯入專案 → 清單正常、git 徽章正常、重建重啟成功
  - watch toggle 開啟 → 改 `.cs` 存檔 → 自動重建重啟該服務；改 `appsettings.json` → 同步重啟不重建
  - 關閉工具 → compose watch 子程序一併結束（工作管理員驗證）
- **驗收基準**：
  - 改一個 `.cs` 存檔 → 該服務重建重啟完成，WSL 軌全程 < 原本（D:\＋WSL2 模式）的 1/5
  - 改 `appsettings.json` → 秒級生效、不觸發 image 重建

## 明確不做（YAGNI）

- 不自動偵測/建議使用者切換 Docker 模式
- 不在工具內產生或驗證 `develop.watch` 區段
- 不支援 `\\wsl$` 以外的網路路徑（一般 UNC share）
- 不動 CMP compose 的 `context: .`（.NET 共用專案需要 root context）
