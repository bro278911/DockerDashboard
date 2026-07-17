# 專案層級 Docker 操作（右鍵選單）設計

日期：2026-07-17

## 背景與目的

目前左側 TreeView 專案節點右鍵選單只有 Git 操作（切換分支、重新整理 Git 狀態）與移除專案；Docker 批次操作（快速啟動、增量重建、強制重建、停止）只有全域按鈕，一次吃全部專案。

使用者痛點：同一專案匯入多個分支資料夾時，想只重啟其中一個分支，得先移除其它分支的專案才能用全域重啟。

目標：專案節點右鍵選單提供四種 Docker 操作，範圍限定該專案的所有 compose 檔。

## UI 變更（MainWindow.xaml）

專案層級 `HierarchicalDataTemplate` 的 ContextMenu，在「重新整理 Git 狀態」之後、「移除專案」之前插入：

```
─────────────
▶ 啟動（快速）          ProjectUpCommand              不重建 image
🔨 增量重建＋啟動        ProjectIncrementalBuildCommand 用 cache，只重建有變動的層
🔨 強制重建＋啟動        ProjectBuildCommand           --no-cache，耗時較長
■ 停止                  ProjectDownCommand
─────────────
（既有）移除專案
```

CommandParameter 均為 `{Binding}`（DockerProject）。重建兩項比照 compose 層級「拉取映像更新」以 `Data.CanRebuild` 控制 IsEnabled。

## ViewModel 變更（MainViewModel.DockerOps.cs）

### 抽共用批次 runner（做法 A）

既有 `AllUpAsync` / `AllUpIncrementalBuildAsync` / `AllUpBuildAsync` / `AllDownAsync` 四支骨架 95% 相同。抽出：

```csharp
private async Task RunComposeBatchAsync(
    IReadOnlyList<ComposeFile> composeFiles,
    Func<string, Action<string>, CancellationToken, Task<(int ExitCode, string Output)>> cliOp,
    string startMessage,   // 開始時 StatusMessage / 首行 log
    string verb,           // 單檔 log 動詞，如「啟動」「增量重建+啟動」
    string successMessage, // 全數成功 StatusMessage
    string failMessage,    // 失敗 StatusMessage 格式（含 {0} 數量）
    int? maxParallel)      // null = 不限並行（停止用）
```

內容 = 現有骨架：cts 建立與置換、IsOperating/IsCancelling、LogLines.Clear、semaphore（maxParallel 有值時）、`_monitor.SuspendRefreshes()`、錯誤彙整 ConcurrentBag、finally 收尾（refreshScope.Dispose、ForceRefreshAsync、旗標復位）、結果 StatusMessage。

四支 All* 變薄殼：組 `Projects.SelectMany(p => p.ComposeFiles)` ＋ 各自訊息與並行度後呼叫 runner。空專案檢查（`Projects.Count == 0`）留在薄殼。

### 新增四支專案層級指令

```csharp
[RelayCommand] private Task ProjectUpAsync(DockerProject? project)
[RelayCommand] private Task ProjectIncrementalBuildAsync(DockerProject? project)
[RelayCommand] private Task ProjectBuildAsync(DockerProject? project)
[RelayCommand] private Task ProjectDownAsync(DockerProject? project)
```

各支：null 檢查後以 `project.ComposeFiles` 呼叫 runner，訊息帶專案名（如「▶ 啟動 {project.Name}（快速模式）」）。

### 並行度（沿用全域版）

| 操作 | CLI 方法 | 並行度 |
|---|---|---|
| 快速啟動 | `ComposeUpFastWithLogAsync` | `Math.Clamp(_batchStartupParallelism, 1, 8)` |
| 增量重建 | `ComposeRebuildRestartWithLogAsync` | 2 |
| 強制重建 | `ComposeForceRebuildWithLogAsync` | 2 |
| 停止 | `ComposeDownWithLogAsync` | 不限 |

## 行為決策

- 單專案操作同樣設 `IsOperating`，與其它操作互斥、可由既有取消鈕取消。
- 多分支容器名衝突不做偵測 — 只對點選專案資料夾跑 compose，衝突由 Docker 自行回報於日誌。
- 不動 compose 層級與服務層級既有選單。

## 驗證

- `dotnet build src/DockerDashboard` 0 error。
- 重構等價性：All* 四支行為不變（同訊息、同並行度、同取消流程），實機比對。
- 實機：匯入兩專案，右鍵單專案跑四種操作，確認只影響該專案容器。
