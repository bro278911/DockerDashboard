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

### 專案環境（ASPNETCORE_ENVIRONMENT）

每個專案可在右鍵「環境」切換 Development／Stage／Production（`AppSettings.ProjectEnvironments`，缺項 = Development；`AllowProductionEnvironment` 預設關，關閉時不列 Production、載入時已存的 Production 回落 Development）。可選項目由 `ProjectEnvironment.DetectAvailable` 掃各 csproj 目錄的 `appsettings.<Env>.json` 決定（背景執行緒）。

- `FastDevComposeGenerator.ServiceOverrideBlock` 依環境寫 `ASPNETCORE_ENVIRONMENT`；非 Development 且有憑證時另寫 `AZURE_TENANT_ID/CLIENT_ID/CLIENT_SECRET`（`$` 跳脫成 `$$`）。Development 輸出與舊版逐字相同。
- 憑證由 `EnvironmentCredentialResolver` 讀服務自己的 `appsettings.<Env>.json` 的 `AppRegistration`，缺欄位時退到同目錄其他服務（AuthenticationBackend 優先）。找不到只記警告、不寫 AZURE_*。憑證值不得進日誌：compose 輸出經 `RedactSecrets` 遮蔽。
- 切到 Production 與以 Production 啟動（`StartServicesAsync` 非 exitFastDev）都走 `ConfirmProductionEnvironment` 二次確認，批次啟動（`StartServicesByDirectoryAsync`）的確認在 Port 衝突停止服務之前、整批只問一次；熱重載與離開 Fast Dev 不重問。`AllowProductionEnvironment` 關閉時直接拒絕切到 Production。環境只影響 Fast Dev 的 .NET 服務，切換後需重啟（重建容器）才生效，由使用者決定是否立刻重啟。
- 「待重啟」標示（`EnvironmentPendingRestart`）由 `RefreshEnvironmentPending` 計算：專案內任一在跑的 Fast Dev 服務，其 `AppliedEnvironment`（容器實際套用的環境，啟動成功時寫入 `AppSettings.AppliedEnvironments`，缺項 = Development）不等於專案選定環境即為待重啟，所以跨目錄分批重啟、重開 App 後都能正確判斷。
- Fast Dev 啟動失敗／取消的回滾（`RollbackFastDevAsync`）依容器實際狀態校正：容器 image 為 `<服務>:fastdev` 保留（或還原）旗標與設定，否則清除；查不到容器或取不到清單才沿用整批還原。
- 批次操作遇取消（`_lastOperationCancelled`）即停止處理後續目錄；可選環境掃描以 `EnvironmentScanVersion` 與 allow 旗標丟棄過期結果。
- 注意：Stage 首次使用要實測 AuthenticationBackend（未驗證其 Stage 設定能在本機容器啟動）；注入的服務主體需有對應 Blob／Key Vault 權限；本機容器會共用同環境的 Data Protection key ring（可能與雲端實例互相影響）。

## 重要慣例

- 停止類批次操作（AllDown/ProjectDown）與 PullAll 使用 `Task.WhenAll` 並行執行
- 所有啟動／套用最新 code／完整重建／離開 Fast Dev 一律走 `MainViewModel.StartServicesAsync(dir, scope)`：只 `up -d --build --no-deps` scope 內服務、override 含目錄內所有 Fast Dev 服務、非 .NET 服務照常 up；不要另寫整包 `up`。容器在跑的「重啟」是 `docker restart`，不經此流程
- 單服務啟動（非完整重建）會依 compose `depends_on` 遞迴帶起「未執行」的依賴（`ExpandDependencies`；已在跑的不動）；up 後若 scope 內容器被重建（ID 改變），會自動 `docker restart` 同目錄中在跑且直接／間接依賴它的服務（nginx 的 proxy_pass 寫死主機名，後端換 IP 後會 502），失敗只警告不回滾
- 熱重載遇操作進行中會記入 `_pendingReloadDirs`，操作結束（`OnIsOperatingChanged`）後補跑；Port 衝突在批次啟動開頭整批只問一次
- 日誌一律用 `docker compose logs -f`（restart 後不中斷）；不再使用 `docker logs -f`
- Views（`SettingsWindow`、`BranchSelectorWindow`）由 `MainViewModel` 直接實例化並呼叫 `ShowDialog()`，不透過 DI
- 所有 UI 更新須透過 `Application.Current?.Dispatcher.InvokeAsync` 回到 UI 執行緒
- `MainViewModel` 實作 `IDisposable`，關閉時清理日誌串流與監控服務
