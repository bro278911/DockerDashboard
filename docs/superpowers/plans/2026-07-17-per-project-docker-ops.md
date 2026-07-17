# 專案層級 Docker 操作 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 左側 TreeView 專案節點右鍵選單提供四種 Docker 操作（快速啟動 / 增量重建＋啟動 / 強制重建＋啟動 / 停止），範圍限定該專案的 compose 檔。

**Architecture:** 先把既有四支 All* 批次指令的重複骨架抽成共用 `RunComposeBatchAsync` runner，All* 變薄殼；再新增四支吃 `DockerProject` 參數的 Project* 指令同走 runner；最後在 XAML 專案層級 ContextMenu 插入四個選單項。

**Tech Stack:** .NET 10 WPF、CommunityToolkit.Mvvm（`[RelayCommand]`）、MaterialDesignThemes 5.x

**Spec:** `docs/superpowers/specs/2026-07-17-per-project-docker-ops-design.md`

## Global Constraints

- 本專案無測試專案 — 每 task 驗證方式為 `dotnet build src/DockerDashboard` 必須 0 errors；行為等價靠重構前後訊息字串與流程逐行比對
- 禁止加註解（除非 WHY 不明顯）
- UI 字串與 StatusMessage 一律繁體中文，沿用既有 emoji 前綴慣例（▶ ■ ✅ ⚠ ⏹ 🔨）
- 不動 compose 層級與服務層級既有選單、不動 `PullAllImagesAsync`
- 所有 UI 綁定沿用既有 `Proxy` StaticResource 模式（`{Binding Data.XxxCommand, Source={StaticResource Proxy}}`）

---

### Task 1: 抽共用批次 runner，All* 四支改薄殼

**Files:**
- Modify: `src/DockerDashboard/ViewModels/MainViewModel.DockerOps.cs:54-381`（`AllUpAsync`、`AllUpIncrementalBuildAsync`、`AllUpBuildAsync`、`AllDownAsync` 四支整段取代，並新增私有 runner）

**Interfaces:**
- Consumes: `IDockerCliService.ComposeUpFastWithLogAsync(string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default)`、`ComposeRebuildRestartWithLogAsync`（同型）、`ComposeForceRebuildWithLogAsync(string, Action<string>, CancellationToken)`、`ComposeDownWithLogAsync(string, Action<string>, CancellationToken)`；`_monitor.SuspendRefreshes()`、`_monitor.ForceRefreshAsync()`、`AppendLog(string)`、`_operationCts`、`IsOperating`/`IsCancelling`/`StatusMessage`/`LogLines`
- Produces: `private Task RunComposeBatchAsync(IReadOnlyList<ComposeFile> composeFiles, Func<string, CancellationToken, Task<(int ExitCode, string Output)>> cliOp, string statusMessage, string headerLog, string verb, string successMessage, string failMessageFormat, int? maxParallel)` — Task 2 直接呼叫

- [ ] **Step 1: 新增 `RunComposeBatchAsync`**

在 `MainViewModel.DockerOps.cs` 的 `AllUpAsync` 前面加入（骨架 = 現有 `AllUpAsync` 55-135 行，差異：composeFiles 由參數傳入、CLI 呼叫改委派、訊息改參數、semaphore 依 `maxParallel` 可選）：

```csharp
    private async Task RunComposeBatchAsync(
        IReadOnlyList<ComposeFile> composeFiles,
        Func<string, CancellationToken, Task<(int ExitCode, string Output)>> cliOp,
        string statusMessage,
        string headerLog,
        string verb,
        string successMessage,
        string failMessageFormat,
        int? maxParallel)
    {
        var cts = new CancellationTokenSource();
        _operationCts?.Dispose();
        _operationCts = cts;
        var ct = cts.Token;

        IsOperating = true;
        IsCancelling = false;
        StatusMessage = statusMessage;
        LogLines.Clear();
        AppendLog($"[{DateTime.Now:HH:mm:ss}] {headerLog}");

        var errors = new ConcurrentBag<string>();
        using var semaphore = maxParallel is int mp ? new SemaphoreSlim(mp) : null;
        IDisposable? refreshScope = _monitor.SuspendRefreshes();

        var tasks = composeFiles.Select(async compose =>
        {
            if (semaphore != null)
            {
                try { await semaphore.WaitAsync(ct); }
                catch (OperationCanceledException) { return; }
            }
            try
            {
                if (ct.IsCancellationRequested) return;
                AppendLog($"[{DateTime.Now:HH:mm:ss}] {verb} {compose.FileName} ...");
                var (exitCode, _) = await cliOp(compose.DirectoryPath, ct);
                if (ct.IsCancellationRequested)
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ {compose.FileName} 已取消");
                else if (exitCode != 0)
                    errors.Add(compose.FileName);
                else
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} {verb}完成");
            }
            catch (OperationCanceledException)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ {compose.FileName} 已取消");
            }
            catch (Exception ex)
            {
                errors.Add($"{compose.FileName}: {ex.Message}");
                AppendLog($"[例外] {ex.Message}");
            }
            finally { semaphore?.Release(); }
        });

        bool wasCancelled = false;
        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            refreshScope?.Dispose();
            wasCancelled = cts.IsCancellationRequested;
            if (ReferenceEquals(_operationCts, cts))
                _operationCts = null;
            cts.Dispose();
            await _monitor.ForceRefreshAsync();
            IsCancelling = false;
            IsOperating = false;
        }

        if (wasCancelled)
            StatusMessage = "⏹ 操作已取消";
        else if (!errors.IsEmpty)
        {
            StatusMessage = string.Format(failMessageFormat, errors.Count);
            foreach (var err in errors)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {err}");
        }
        else
            StatusMessage = successMessage;
    }
```

- [ ] **Step 2: 四支 All* 整段換成薄殼**

用下列四支**完整取代**原 `AllUpAsync`（54-136 行）、`AllUpIncrementalBuildAsync`（138-219 行）、`AllUpBuildAsync`（221-302 行）、`AllDownAsync`（304-381 行）：

```csharp
    [RelayCommand]
    private async Task AllUpAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        await RunComposeBatchAsync(
            Projects.SelectMany(p => p.ComposeFiles).ToList(),
            (dir, ct) => _dockerCli.ComposeUpFastWithLogAsync(dir, AppendLog, ct: ct),
            "正在並行啟動所有服務（快速模式）...",
            "▶ 全部啟動（快速模式，不重建 image）",
            "啟動",
            "✅ 所有服務已啟動",
            "⚠ {0} 個服務啟動失敗",
            Math.Clamp(_batchStartupParallelism, 1, 8));
    }

    [RelayCommand]
    private async Task AllUpIncrementalBuildAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        await RunComposeBatchAsync(
            Projects.SelectMany(p => p.ComposeFiles).ToList(),
            (dir, ct) => _dockerCli.ComposeRebuildRestartWithLogAsync(dir, AppendLog, ct: ct),
            "正在並行增量重建所有 image（使用 cache，速度快）...",
            "▶ 全部增量重建（使用 cache，只重建有變動的層）",
            "增量重建+啟動",
            "✅ 所有服務已增量重建並啟動",
            "⚠ {0} 個服務增量重建失敗",
            2);
    }

    [RelayCommand]
    private async Task AllUpBuildAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        await RunComposeBatchAsync(
            Projects.SelectMany(p => p.ComposeFiles).ToList(),
            (dir, ct) => _dockerCli.ComposeForceRebuildWithLogAsync(dir, AppendLog, ct),
            "正在強制重建所有 image 並啟動（不使用 cache）...",
            "▶ 全部強制重建（--no-cache，耗時較長）",
            "強制重建+啟動",
            "✅ 所有服務已重建並啟動",
            "⚠ {0} 個服務重建失敗",
            2);
    }

    [RelayCommand]
    private async Task AllDownAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        await RunComposeBatchAsync(
            Projects.SelectMany(p => p.ComposeFiles).ToList(),
            (dir, ct) => _dockerCli.ComposeDownWithLogAsync(dir, AppendLog, ct),
            "正在並行停止所有服務...",
            "■ 全部停止（並行）",
            "停止",
            "✅ 所有服務已停止",
            "⚠ {0} 個服務停止失敗",
            null);
    }
```

行為等價備註（審查對照用）：
- 原 `AllUpIncrementalBuildAsync`/`AllUpBuildAsync` 的 `using var semaphore = new SemaphoreSlim(2)` → `maxParallel: 2`
- 原 `AllDownAsync` 無 semaphore → `maxParallel: null`
- 原各支訊息字串逐字保留於參數
- 原 `AllUpAsync` 首行 log「▶ 全部啟動（快速模式，不重建 image）」動詞「啟動」等對照不變

- [ ] **Step 3: Build 驗證**

Run: `dotnet build src/DockerDashboard`
Expected: `0 個錯誤`（0 errors；warnings 可接受）

- [ ] **Step 4: Commit**

```bash
git add src/DockerDashboard/ViewModels/MainViewModel.DockerOps.cs
git commit -m "refactor: 抽共用 compose 批次 runner，All* 指令改薄殼"
```

---

### Task 2: 新增四支專案層級指令

**Files:**
- Modify: `src/DockerDashboard/ViewModels/MainViewModel.DockerOps.cs`（`AllDownAsync` 之後插入四支新指令）

**Interfaces:**
- Consumes: Task 1 的 `RunComposeBatchAsync`（簽章見 Task 1 Produces）；`DockerProject`（`Name`、`ComposeFiles` 屬性，定義於 `src/DockerDashboard/Models/DockerProject.cs`）
- Produces: `ProjectUpCommand`、`ProjectIncrementalBuildCommand`、`ProjectBuildCommand`、`ProjectDownCommand`（`[RelayCommand]` 自動生成，參數 `DockerProject?`）— Task 3 XAML 綁定用

- [ ] **Step 1: 加入四支指令**

在 `AllDownAsync` 方法之後插入：

```csharp
    [RelayCommand]
    private async Task ProjectUpAsync(DockerProject? project)
    {
        if (project == null) return;

        await RunComposeBatchAsync(
            project.ComposeFiles.ToList(),
            (dir, ct) => _dockerCli.ComposeUpFastWithLogAsync(dir, AppendLog, ct: ct),
            $"正在啟動 {project.Name}（快速模式）...",
            $"▶ 啟動 {project.Name}（快速模式，不重建 image）",
            "啟動",
            $"✅ {project.Name} 已啟動",
            "⚠ {0} 個服務啟動失敗",
            Math.Clamp(_batchStartupParallelism, 1, 8));
    }

    [RelayCommand]
    private async Task ProjectIncrementalBuildAsync(DockerProject? project)
    {
        if (project == null) return;

        await RunComposeBatchAsync(
            project.ComposeFiles.ToList(),
            (dir, ct) => _dockerCli.ComposeRebuildRestartWithLogAsync(dir, AppendLog, ct: ct),
            $"正在增量重建 {project.Name}（使用 cache）...",
            $"▶ 增量重建 {project.Name}（使用 cache，只重建有變動的層）",
            "增量重建+啟動",
            $"✅ {project.Name} 已增量重建並啟動",
            "⚠ {0} 個服務增量重建失敗",
            2);
    }

    [RelayCommand]
    private async Task ProjectBuildAsync(DockerProject? project)
    {
        if (project == null) return;

        await RunComposeBatchAsync(
            project.ComposeFiles.ToList(),
            (dir, ct) => _dockerCli.ComposeForceRebuildWithLogAsync(dir, AppendLog, ct),
            $"正在強制重建 {project.Name}（不使用 cache）...",
            $"▶ 強制重建 {project.Name}（--no-cache，耗時較長）",
            "強制重建+啟動",
            $"✅ {project.Name} 已重建並啟動",
            "⚠ {0} 個服務重建失敗",
            2);
    }

    [RelayCommand]
    private async Task ProjectDownAsync(DockerProject? project)
    {
        if (project == null) return;

        await RunComposeBatchAsync(
            project.ComposeFiles.ToList(),
            (dir, ct) => _dockerCli.ComposeDownWithLogAsync(dir, AppendLog, ct),
            $"正在停止 {project.Name}...",
            $"■ 停止 {project.Name}",
            "停止",
            $"✅ {project.Name} 已停止",
            "⚠ {0} 個服務停止失敗",
            null);
    }
```

- [ ] **Step 2: Build 驗證**

Run: `dotnet build src/DockerDashboard`
Expected: `0 個錯誤`

- [ ] **Step 3: Commit**

```bash
git add src/DockerDashboard/ViewModels/MainViewModel.DockerOps.cs
git commit -m "feat: 新增專案層級 Docker 批次指令"
```

---

### Task 3: 專案節點右鍵選單加四項

**Files:**
- Modify: `src/DockerDashboard/MainWindow.xaml:215-243`（專案層級 `HierarchicalDataTemplate` 的 `<StackPanel.ContextMenu>`）

**Interfaces:**
- Consumes: Task 2 的 `ProjectUpCommand` / `ProjectIncrementalBuildCommand` / `ProjectBuildCommand` / `ProjectDownCommand`；既有 `Proxy` StaticResource、`CanRebuild` 屬性（`MainViewModel.cs:84`）
- Produces: 無（終端 UI）

- [ ] **Step 1: 插入選單項**

在「重新整理 Git 狀態」`MenuItem`（結尾於 `MainWindow.xaml:232` 附近）與既有 `<Separator />`＋「移除專案」之間插入：

```xml
                                            <Separator />
                                            <MenuItem Header="啟動（快速）"
                                                      Command="{Binding Data.ProjectUpCommand, Source={StaticResource Proxy}}"
                                                      CommandParameter="{Binding}"
                                                      ToolTip="啟動此專案全部服務，不重建 image">
                                                <MenuItem.Icon>
                                                    <materialDesign:PackIcon Kind="Play" />
                                                </MenuItem.Icon>
                                            </MenuItem>
                                            <MenuItem Header="增量重建＋啟動"
                                                      Command="{Binding Data.ProjectIncrementalBuildCommand, Source={StaticResource Proxy}}"
                                                      CommandParameter="{Binding}"
                                                      IsEnabled="{Binding Data.CanRebuild, Source={StaticResource Proxy}}"
                                                      ToolTip="使用 cache，只重建有變動的層後啟動">
                                                <MenuItem.Icon>
                                                    <materialDesign:PackIcon Kind="Hammer" />
                                                </MenuItem.Icon>
                                            </MenuItem>
                                            <MenuItem Header="強制重建＋啟動"
                                                      Command="{Binding Data.ProjectBuildCommand, Source={StaticResource Proxy}}"
                                                      CommandParameter="{Binding}"
                                                      IsEnabled="{Binding Data.CanRebuild, Source={StaticResource Proxy}}"
                                                      ToolTip="--no-cache 完整重建後啟動，耗時較長">
                                                <MenuItem.Icon>
                                                    <materialDesign:PackIcon Kind="HammerWrench" />
                                                </MenuItem.Icon>
                                            </MenuItem>
                                            <MenuItem Header="停止"
                                                      Command="{Binding Data.ProjectDownCommand, Source={StaticResource Proxy}}"
                                                      CommandParameter="{Binding}"
                                                      ToolTip="停止此專案全部服務">
                                                <MenuItem.Icon>
                                                    <materialDesign:PackIcon Kind="Stop" />
                                                </MenuItem.Icon>
                                            </MenuItem>
```

插入後選單順序：切換分支 → 重新整理 Git 狀態 → ─ → 啟動（快速）→ 增量重建＋啟動 → 強制重建＋啟動 → 停止 → ─（既有）→ 移除專案。

- [ ] **Step 2: Build 驗證**

Run: `dotnet build src/DockerDashboard`
Expected: `0 個錯誤`（XAML 編譯含 PackIcon Kind 名稱驗證；若 `HammerWrench` 不存在會編譯期報 XAML 錯，改用 `Hammer`）

- [ ] **Step 3: Commit**

```bash
git add src/DockerDashboard/MainWindow.xaml
git commit -m "feat: 專案右鍵選單新增四種 Docker 操作"
```

---

### Task 4: 實機驗證

**Files:** 無變更（驗證）

**Interfaces:**
- Consumes: Task 1-3 全部成果

- [ ] **Step 1: 啟動 app**

Run: `dotnet run --project src/DockerDashboard`
Expected: 主視窗開啟，左側專案樹正常。

- [ ] **Step 2: 右鍵驗證**

匯入至少兩個專案，對其中一個專案節點右鍵：
- 四個新選單項出現且圖示正確
- 「啟動（快速）」只啟動該專案容器，另一專案容器不動（`docker ps` 對照）
- 「停止」只停該專案
- 操作期間全域按鈕反灰（`IsOperating` 互斥）、取消鈕可中斷
- 「增量重建＋啟動」在操作進行中反灰（`CanRebuild`）

- [ ] **Step 3: 全域回歸**

- 「全部啟動」「全部停止」行為與訊息與改版前一致（對照日誌動詞與完成訊息）

- [ ] **Step 4: 驗證結果回報使用者後結束**
