# Docker Dashboard

Windows WPF 桌面工具，用於集中管理多個 Docker Compose 專案。支援一鍵啟動/停止所有服務、即時日誌串流、Git 分支切換與系統匣常駐。

## 功能

- **多專案管理**：匯入多個含有 `docker-compose.yml` 的資料夾，集中檢視所有服務狀態
- **批次操作**：一鍵並行啟動 / 停止 / 重建所有專案的服務
- **單一服務控制**：針對個別服務執行啟動、停止、重啟
- **即時日誌**：串流顯示容器輸出，支援關鍵字過濾與匯出
- **Git 整合**：顯示目前分支與 dirty 狀態，支援圖形化切換本地/遠端分支
- **容器監控**：定期輪詢容器狀態，崩潰時以系統匣氣球提示通知
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

程式設定（匯入的資料夾清單、輪詢間隔、Compose 版本偏好）自動儲存於：

```
%APPDATA%\DockerDashboard\settings.json
```
