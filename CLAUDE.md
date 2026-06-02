# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 專案概覽

Windows WPF 桌面工具，集中管理多個 Docker Compose 專案。單一 C# 專案，無 solution 檔。

## 常用指令

```powershell
# 建置
dotnet build src/DockerDashboard

# 執行（開發用）
dotnet run --project src/DockerDashboard

# 發布（自包含，win-x64）
dotnet publish src/DockerDashboard -c Release -r win-x64 --self-contained false -o publish

# 格式化
dotnet format src/DockerDashboard
```

## 架構

### 技術棧

- .NET 10 WPF，`net10.0-windows`
- MVVM：CommunityToolkit.Mvvm（`[ObservableProperty]`、`[RelayCommand]`）
- UI：MaterialDesignThemes 5.x
- DI：Microsoft.Extensions.DependencyInjection（所有服務均為 Singleton）
- YAML 解析：YamlDotNet（ComposeFileScanner fallback 用）

### DI 設定（`App.xaml.cs`）

所有服務、ViewModel、MainWindow 均在啟動時注入為 Singleton。`DockerCliService` 同時以具體型別和介面 `IDockerCliService` 註冊（方便測試替換）。

### 核心資料流

```
MainWindow
  └── MainViewModel（唯一 ViewModel）
        ├── IDockerCliService  → 包裝 docker/docker-compose CLI 呼叫
        ├── IGitService        → 包裝 git CLI 呼叫
        ├── ComposeFileScanner → 掃描資料夾尋找 compose 檔
        ├── SettingsService    → 讀寫 %APPDATA%\DockerDashboard\settings.json
        └── ContainerMonitorService → 雙迴圈監控（PeriodicTimer + docker events stream）
```

### ComposeFileScanner 掃描策略

1. **優先**：執行 `docker compose config --format json` 解析（支援變數展開、merge key、多檔合併）
2. **Fallback**：Docker CLI 不可用時改用 YamlDotNet 手動解析 YAML

掃描時自動跳過 `obj/`、`bin/`、`node_modules/`、`.git/`、`packages/` 目錄。  
支援的 compose 檔名：`docker-compose.yml/yaml`、`compose.yml/yaml`，並自動合併同目錄的 `override` 和 `build` 變體。

### ContainerMonitorService 雙迴圈

- **MonitorLoop**：`PeriodicTimer` 定期輪詢 `docker ps` 更新狀態
- **EventsLoop**：串流 `docker events` 即時偵測容器狀態變化（start/stop/die/kill 等），收到事件後觸發 `ForceRefreshAsync`

容器崩潰偵測：比對前後快照，若 `Running → Exited/Dead` 則觸發 `ContainerCrashed` 事件，MainViewModel 透過系統匣氣球通知使用者。

### Docker 模式

`IDockerCliService` 支援兩種執行模式（在 `AppSettings.DockerMode` 設定）：
- `DockerMode.DockerDesktop`：直接呼叫 `docker`
- `DockerMode.Wsl2`：透過 `wsl -d <distro> docker` 呼叫

### 容器狀態比對（`MainViewModel.OnContainersUpdated`）

三種比對策略（依序）：
1. 比對 `ContainerName`（`container_name:` 欄位）
2. 比對 docker compose label `com.docker.compose.service=<name>`
3. 比對容器名稱與 service 名稱

### 日誌管理

- 上限 5000 行；超過時保留後 4500 行（捨棄最舊 500 行）
- 支援關鍵字即時過濾（`ICollectionView.Filter`）
- 可匯出為 `.txt`/`.log` 檔

### 設定持久化

`%APPDATA%\DockerDashboard\settings.json`，由 `SettingsService` 以 JSON 讀寫，包含：匯入的資料夾清單、最近移除的資料夾（最多 10 筆）、輪詢間隔、Docker 模式、WSL2 Distro 名稱。

## 重要慣例

- 所有 Docker 批次操作（AllUp/AllDown/PullAll）使用 `Task.WhenAll` 並行執行
- Views（`SettingsWindow`、`BranchSelectorWindow`）由 `MainViewModel` 直接實例化並呼叫 `ShowDialog()`，不透過 DI
- 所有 UI 更新須透過 `Application.Current?.Dispatcher.InvokeAsync` 回到 UI 執行緒
- `MainViewModel` 實作 `IDisposable`，關閉時清理日誌串流與監控服務
