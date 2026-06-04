# Docker Dashboard

Windows WPF 桌面工具，用於集中管理多個 Docker Compose 專案。支援一鍵啟動/停止所有服務、即時日誌串流、Git 分支切換與系統匣常駐。

## 功能

- **多專案管理**：匯入多個含有 `docker-compose.yml` 的資料夾，集中檢視所有服務狀態
- **批次操作**：一鍵並行啟動 / 停止；強制重建（`--no-cache`）所有 image 後依序啟動
- **單一服務控制**：針對個別服務執行啟動、停止、快速重啟（`docker compose restart`）、重建重啟（`up -d --build`）
- **即時日誌**：串流顯示容器輸出，支援關鍵字過濾與匯出
- **Git 整合**：顯示目前分支與 dirty 狀態，支援圖形化切換本地/遠端分支
- **容器監控**：定期輪詢容器狀態，崩潰時以系統匣氣球提示通知
- **自動監控重建**：監聽原始碼變動，自動重新 build image 並重啟容器（可針對單一服務開關）
- **Docker 修復工具**：一鍵清除懸空 image、未使用 image/volume，或執行完整系統清除
- **系統匣常駐**：最小化後縮入系統匣，雙擊還原視窗

## 系統需求

| 項目 | 版本 |
|------|------|
| OS | Windows 10 / 11 (x64) |
| Runtime | .NET 10 Desktop Runtime |
| Docker | Docker Desktop（含 Docker Compose v2）或 Docker Engine + Compose Plugin |
| Git | 選用，啟用 Git 整合功能時需要 |

## 快速開始

### 直接執行（無需安裝）

1. 前往 [Releases](../../releases) 下載最新版 `DockerDashboard.exe`
2. 確認 Docker Desktop 已啟動
3. 執行 `DockerDashboard.exe`

### 從原始碼建置

```bash
git clone <repo-url>
cd src/DockerDashboard
dotnet publish -c Release -r win-x64 --self-contained false -o ../../publish
```

## 使用方式

1. 啟動程式後，點擊工具列的「匯入資料夾」按鈕
2. 選擇一個或多個含有 `docker-compose.yml` 的專案根目錄
3. 左側樹狀清單顯示所有專案與服務，右側顯示即時日誌
4. 使用頂部按鈕批次操作，或右鍵選單針對個別服務操作

### 操作說明

| 操作 | 說明 |
|------|------|
| 全部啟動 | `docker compose up -d`，並行執行所有專案 |
| 全部停止 | `docker compose down`，並行執行所有專案 |
| 重建啟動 | `docker compose build --no-cache` + `up -d`，強制從頭重建所有 image（循序執行，耗時較長）|
| 重啟（快速） | `docker compose restart`，只重啟 process，不重建 image |
| 重建重啟 | `docker compose up -d --build --no-deps`，重新 build image 後重啟（改動程式碼後使用）|

### 自動監控重建（Auto Watch & Rebuild）

對指定服務啟用後，`WatchRebuildService` 會監聽該服務的工作目錄（使用 `FileSystemWatcher`），偵測到原始碼異動時自動觸發 `docker compose up -d --build --no-deps`。

- **防抖延遲**：預設 2 秒（可在設定中調整），連續變動只觸發一次重建
- **跳過目錄**：`.git`、`bin`、`obj`、`node_modules` 的異動不觸發
- **全域開關**：設定視窗 → 「Auto Watch & Rebuild」ToggleButton（需先開啟才能生效）
- **個別服務**：主畫面服務列右側 Watch 圖示按鈕（或右鍵選單）獨立開關每個服務
- **持久化**：啟用清單（`WatchEnabledServiceKeys`）和防抖秒數（`WatchDebounceSeconds`）儲存於 `settings.json`
- **啟動平行度**：可於設定調整「全部啟動平行度」（1–8，建議 2–4）以平衡速度與資源競爭

### Docker 修復工具

透過工具列按鈕開啟，提供下列清除操作（可複選）：

| 操作 | 指令 | 風險等級 |
|------|------|----------|
| 清除懸空 Image | `docker image prune -f` | 低 |
| 清除所有未使用 Image | `docker image prune -af` | 中 |
| 清除未使用 Volume | `docker volume prune -f` | 中（注意資料遺失）|
| 完整系統清除 | `docker system prune -af --volumes` | 高 |

## 技術棧

- **框架**：.NET 10 WPF
- **MVVM**：CommunityToolkit.Mvvm
- **UI**：MaterialDesignThemes 5.x
- **YAML 解析**：YamlDotNet
- **DI**：Microsoft.Extensions.Hosting

## 專案結構

```
src/DockerDashboard/
├── Models/          # 資料模型（DockerProject, DockerService, ContainerInfo...）
├── ViewModels/      # MVVM ViewModel（MainViewModel）
├── Views/           # 額外視窗（SettingsWindow, BranchSelectorWindow）
├── Services/        # 業務邏輯（DockerCliService, GitService, ContainerMonitorService...）
├── Converters/      # WPF 值轉換器
└── Assets/          # 圖示資源
```

## 設定

程式設定（匯入的資料夾清單、輪詢間隔、Compose 版本偏好、Docker 模式）自動儲存於：

```
%APPDATA%\DockerDashboard\settings.json
```

### Docker 模式

| 模式 | 說明 |
|------|------|
| Docker Desktop | 直接呼叫 `docker`（預設）|
| WSL2 | 透過 `wsl -d <distro>` 呼叫，需設定 Distro 名稱 |
