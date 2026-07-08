# WSL/Windows 兩軌重建效能優化 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 本工具支援 WSL ext4 專案（`\\wsl$` 路徑）、build 加 `COMPOSE_BAKE`、WSL 專案的 Auto Watch 改走 `docker compose watch` 常駐子程序。

**Architecture:** 三個獨立改動：(1) `ConvertToWslPath` 加 UNC 解析＋`IsWslUncPath` 判斷；(2) `RunCommandWithLogAsync` 的 `withBuildEnv` 區塊補 `COMPOSE_BAKE=true`；(3) 新 `ComposeWatchService` 管理每個 compose 目錄一支 `docker compose watch --no-up` 子程序，`MainViewModel` 依路徑型別路由到新舊兩種 watch 機制。

**Tech Stack:** .NET 10 WPF、CommunityToolkit.Mvvm、xUnit（tests/DockerDashboard.Tests）

**Spec:** `docs/superpowers/specs/2026-07-08-wsl-windows-build-perf-design.md`

## Global Constraints

- 回覆與程式碼註解使用繁體中文；註解只寫非顯而易見的 WHY
- 所有 UI 更新透過 `Application.Current?.Dispatcher.InvokeAsync` 回 UI 執行緒
- DI 服務全部 Singleton（`App.xaml.cs`）
- 不認得的 UNC（非 `wsl$`/`wsl.localhost`）原樣回傳，不拋例外
- compose watch 程序異常退出：記日誌＋該目錄所有 watch toggle 自動關閉
- 工具關閉時 compose watch 子程序必須一併終止（kill process tree）
- 驗證指令：`dotnet build src/DockerDashboard --no-incremental -v minimal`（0 errors）、`dotnet test tests/DockerDashboard.Tests -v minimal`（全過）

---

### Task 1: ConvertToWslPath UNC 支援 + IsWslUncPath

**Files:**
- Modify: `src/DockerDashboard/Services/DockerCliService.cs:28-38`（`ConvertToWslPath`，緊接其後加 `IsWslUncPath`）
- Test: `tests/DockerDashboard.Tests/WslPathTests.cs`（新建）

**Interfaces:**
- Produces: `public static string DockerCliService.ConvertToWslPath(string windowsPath)`（既有簽名不變，行為擴充）；`public static bool DockerCliService.IsWslUncPath(string path)`（Task 4/5 用來路由 watch 機制）

- [ ] **Step 1: 寫失敗測試**

建立 `tests/DockerDashboard.Tests/WslPathTests.cs`：

```csharp
using System.IO;
using DockerDashboard.Services;
using Xunit;

namespace DockerDashboard.Tests;

public class WslPathTests
{
    [Theory]
    [InlineData(@"D:\projects\app", "/mnt/d/projects/app")]
    [InlineData(@"C:/projects/app", "/mnt/c/projects/app")]
    [InlineData(@"\\wsl$\Ubuntu\home\user\app", "/home/user/app")]
    [InlineData(@"\\wsl.localhost\Ubuntu-22.04\home\user\app", "/home/user/app")]
    [InlineData(@"\\WSL$\Ubuntu\home\user", "/home/user")]
    [InlineData(@"\\wsl$\Ubuntu\home\user\", "/home/user/")]
    [InlineData(@"\\wsl$\Ubuntu", "/")]
    [InlineData(@"\\server\share\folder", @"\\server\share\folder")]
    [InlineData("", "")]
    public void ConvertToWslPath_轉換各種路徑(string input, string expected)
    {
        Assert.Equal(expected, DockerCliService.ConvertToWslPath(input));
    }

    [Theory]
    [InlineData(@"\\wsl$\Ubuntu\home\user\app", true)]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\user\app", true)]
    [InlineData(@"\\WSL.LOCALHOST\Ubuntu\home", true)]
    [InlineData(@"D:\projects\app", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData("", false)]
    public void IsWslUncPath_判斷是否為WSL路徑(string input, bool expected)
    {
        Assert.Equal(expected, DockerCliService.IsWslUncPath(input));
    }
}
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test tests/DockerDashboard.Tests --filter WslPathTests -v minimal`
Expected: FAIL — `IsWslUncPath` 不存在（編譯錯誤），或 UNC 案例 Assert 失敗

- [ ] **Step 3: 實作**

`DockerCliService.cs` 將 `ConvertToWslPath` 改為（並在其後加 `IsWslUncPath`）：

```csharp
    private static readonly Regex WslUncRegex = new(
        @"^[\\/]{2}wsl(\$|\.localhost)[\\/]([^\\/]+)([\\/].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ConvertToWslPath(string windowsPath)
    {
        if (string.IsNullOrEmpty(windowsPath)) return windowsPath;

        var uncMatch = WslUncRegex.Match(windowsPath);
        if (uncMatch.Success)
        {
            var rest = uncMatch.Groups[3].Value.Replace('\\', '/');
            return rest.Length == 0 ? "/" : rest;
        }

        var match = Regex.Match(windowsPath, @"^([A-Za-z]):[\\\/](.*)$");
        if (!match.Success) return windowsPath;

        var drive = match.Groups[1].Value.ToLowerInvariant();
        var rest2 = match.Groups[2].Value.Replace('\\', '/');
        return $"/mnt/{drive}/{rest2}";
    }

    public static bool IsWslUncPath(string path) =>
        !string.IsNullOrEmpty(path) && WslUncRegex.IsMatch(path);
```

注意：原本第二個 regex 的區域變數名 `rest` 改為 `rest2` 避免重名（或維持原程式碼區塊命名，實作者自行調整編譯通過即可，語意不變）。

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test tests/DockerDashboard.Tests --filter WslPathTests -v minimal`
Expected: PASS（15 案例全過）

- [ ] **Step 5: 全量驗證＋commit**

```bash
dotnet build src/DockerDashboard --no-incremental -v minimal
dotnet test tests/DockerDashboard.Tests -v minimal
git add src/DockerDashboard/Services/DockerCliService.cs tests/DockerDashboard.Tests/WslPathTests.cs
git commit -m "feat: ConvertToWslPath 支援 wsl$ UNC 路徑"
```

---

### Task 2: build 指令加 COMPOSE_BAKE=true

**Files:**
- Modify: `src/DockerDashboard/Services/DockerCliService.cs:348-364`（`RunCommandWithLogAsync` 開頭的 env 區塊，抽出 `GetBuildEnv`）
- Test: `tests/DockerDashboard.Tests/BuildEnvTests.cs`（新建）

**Interfaces:**
- Produces: `public static Dictionary<string, string> DockerCliService.GetBuildEnv(int buildKitParallelism)` — 回傳 build 類指令要加的環境變數
- 行為不變處：`DOCKER_BUILDKIT=1`、`COMPOSE_DOCKER_CLI_BUILD=1` 照舊對所有指令設定

- [ ] **Step 1: 寫失敗測試**

建立 `tests/DockerDashboard.Tests/BuildEnvTests.cs`：

```csharp
using DockerDashboard.Services;
using Xunit;

namespace DockerDashboard.Tests;

public class BuildEnvTests
{
    [Fact]
    public void GetBuildEnv_永遠包含COMPOSE_BAKE()
    {
        var env = DockerCliService.GetBuildEnv(0);
        Assert.Equal("true", env["COMPOSE_BAKE"]);
        Assert.False(env.ContainsKey("BUILDKIT_MAX_PARALLELISM"));
    }

    [Fact]
    public void GetBuildEnv_平行度大於零時包含BUILDKIT_MAX_PARALLELISM()
    {
        var env = DockerCliService.GetBuildEnv(4);
        Assert.Equal("true", env["COMPOSE_BAKE"]);
        Assert.Equal("4", env["BUILDKIT_MAX_PARALLELISM"]);
    }
}
```

- [ ] **Step 2: 跑測試確認失敗**

Run: `dotnet test tests/DockerDashboard.Tests --filter BuildEnvTests -v minimal`
Expected: FAIL — `GetBuildEnv` 不存在（編譯錯誤）

- [ ] **Step 3: 實作**

`DockerCliService.cs`：新增 `GetBuildEnv`，並改寫 `RunCommandWithLogAsync` 開頭：

```csharp
    public static Dictionary<string, string> GetBuildEnv(int buildKitParallelism)
    {
        var env = new Dictionary<string, string> { ["COMPOSE_BAKE"] = "true" };
        if (buildKitParallelism > 0)
            env["BUILDKIT_MAX_PARALLELISM"] = buildKitParallelism.ToString();
        return env;
    }
```

`RunCommandWithLogAsync` 內原本：

```csharp
        IReadOnlyDictionary<string, string>? wslEnvOverrides = null;
        if (withBuildEnv && IsWsl2 && BuildKitParallelism > 0)
            wslEnvOverrides = new Dictionary<string, string>
            {
                ["BUILDKIT_MAX_PARALLELISM"] = BuildKitParallelism.ToString()
            };

        var psi = CreatePsi(command, args, workingDirectory, wslEnvOverrides);
        psi.Environment["DOCKER_BUILDKIT"] = "1";
        psi.Environment["COMPOSE_DOCKER_CLI_BUILD"] = "1";
        if (withBuildEnv && !IsWsl2 && BuildKitParallelism > 0)
            psi.Environment["BUILDKIT_MAX_PARALLELISM"] = BuildKitParallelism.ToString();
```

改為：

```csharp
        IReadOnlyDictionary<string, string>? wslEnvOverrides = null;
        if (withBuildEnv && IsWsl2)
            wslEnvOverrides = GetBuildEnv(BuildKitParallelism);

        var psi = CreatePsi(command, args, workingDirectory, wslEnvOverrides);
        psi.Environment["DOCKER_BUILDKIT"] = "1";
        psi.Environment["COMPOSE_DOCKER_CLI_BUILD"] = "1";
        if (withBuildEnv && !IsWsl2)
            foreach (var kv in GetBuildEnv(BuildKitParallelism))
                psi.Environment[kv.Key] = kv.Value;
```

同步更新第 348 行註解為：`// withBuildEnv=true 時設定 COMPOSE_BAKE 與 BUILDKIT_MAX_PARALLELISM`

- [ ] **Step 4: 跑測試確認通過**

Run: `dotnet test tests/DockerDashboard.Tests --filter BuildEnvTests -v minimal`
Expected: PASS（2 案例）

- [ ] **Step 5: 全量驗證＋commit**

```bash
dotnet build src/DockerDashboard --no-incremental -v minimal
dotnet test tests/DockerDashboard.Tests -v minimal
git add -A
git commit -m "feat: build 指令啟用 COMPOSE_BAKE"
```

---

### Task 3: StartComposeWatch CLI 方法 + ProcessStream 曝露退出狀態

**Files:**
- Modify: `src/DockerDashboard/Services/IDockerCliService.cs`（`StartComposeLogStream` 宣告後加一行）
- Modify: `src/DockerDashboard/Services/DockerCliService.cs`（`StartComposeLogStream` 實作後加方法）
- Modify: `src/DockerDashboard/Services/ProcessStream.cs`（加 `ExitCode`）

**Interfaces:**
- Produces: `ProcessStream IDockerCliService.StartComposeWatch(string workingDirectory, IEnumerable<string> serviceNames)`；`int ProcessStream.ExitCode` — Task 4 的 `ComposeWatchService` 消費
- 無測試（薄包裝，程序啟動類方法與既有 `StartComposeLogStream` 同模式，無單元測試前例；由 Task 6 手動冒煙涵蓋）

- [ ] **Step 1: IDockerCliService 加宣告**

`IDockerCliService.cs` 在 `StartComposeLogStream` 宣告後加：

```csharp
    ProcessStream StartComposeWatch(string workingDirectory, IEnumerable<string> serviceNames);
```

- [ ] **Step 2: DockerCliService 加實作**

`DockerCliService.cs` 在 `StartComposeLogStream` 實作後加：

```csharp
    public ProcessStream StartComposeWatch(string workingDirectory, IEnumerable<string> serviceNames)
    {
        var args = BuildComposeArgs(workingDirectory, ["watch", "--no-up"]);
        args.AddRange(serviceNames);
        var psi = CreatePsi(ComposeCommand, args, workingDirectory);

        var process = new Process { StartInfo = psi };
        process.Start();
        return new ProcessStream(process);
    }
```

- [ ] **Step 3: ProcessStream 曝露退出碼**

`ProcessStream.cs` 在 `StandardError` 屬性後加：

```csharp
    public int ExitCode => _process.ExitCode;
```

- [ ] **Step 4: 全量驗證＋commit**

```bash
dotnet build src/DockerDashboard --no-incremental -v minimal
dotnet test tests/DockerDashboard.Tests -v minimal
git add -A
git commit -m "feat: 新增 compose watch 串流啟動方法"
```

---

### Task 4: ComposeWatchService（compose watch 子程序管理）

**Files:**
- Create: `src/DockerDashboard/Services/ComposeWatchService.cs`
- Modify: `src/DockerDashboard/App.xaml.cs`（DI 註冊，`services.AddSingleton<WatchRebuildService>()` 附近加一行）

**Interfaces:**
- Consumes: `IDockerCliService.StartComposeWatch(workingDirectory, serviceNames)`（Task 3）、`ProcessStream.StandardOutput/StandardError/HasExited/ExitCode/Kill/Dispose`
- Produces: `public class ComposeWatchService : IDisposable`，成員：
  - `Action<string>? OnOutput`（每行輸出，含目錄前綴）
  - `Action<string, int>? OnProcessExited`（參數＝workingDirectory、exitCode，僅異常退出時觸發）
  - `void SetWatchedServices(string workingDirectory, IReadOnlyCollection<string> serviceNames)`（空集合＝停止該目錄程序）
  - `void ClearAll()`、`void Dispose()`
- 無單元測試：核心是子程序生命週期管理，與 `WatchRebuildService`/`ContainerMonitorService` 同類（本專案此類服務無單元測試前例），Task 6 手動冒煙涵蓋

- [ ] **Step 1: 建立 ComposeWatchService.cs**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DockerDashboard.Services;

public class ComposeWatchService : IDisposable
{
    private readonly IDockerCliService _dockerCli;
    private readonly Dictionary<string, WatchEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    public Action<string>? OnOutput { get; set; }
    public Action<string, int>? OnProcessExited { get; set; }

    public ComposeWatchService(IDockerCliService dockerCli) => _dockerCli = dockerCli;

    private sealed class WatchEntry
    {
        public required ProcessStream Stream { get; init; }
        public required HashSet<string> Services { get; init; }
        // 主動停止時設 true，PumpAsync 據此區分異常退出
        public bool StoppedByUs;
    }

    public void SetWatchedServices(string workingDirectory, IReadOnlyCollection<string> serviceNames)
    {
        lock (_lock)
        {
            if (_disposed) return;

            if (_entries.TryGetValue(workingDirectory, out var existing))
            {
                if (existing.Services.SetEquals(serviceNames)) return;
                existing.StoppedByUs = true;
                existing.Stream.Kill();
                _entries.Remove(workingDirectory);
            }

            if (serviceNames.Count == 0) return;

            var entry = new WatchEntry
            {
                Stream = _dockerCli.StartComposeWatch(workingDirectory, serviceNames),
                Services = new HashSet<string>(serviceNames, StringComparer.OrdinalIgnoreCase)
            };
            _entries[workingDirectory] = entry;
            _ = PumpAsync(workingDirectory, entry);
        }
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            foreach (var entry in _entries.Values)
            {
                entry.StoppedByUs = true;
                entry.Stream.Kill();
            }
            _entries.Clear();
        }
    }

    private async Task PumpAsync(string workingDirectory, WatchEntry entry)
    {
        var name = Path.GetFileName(workingDirectory.TrimEnd('\\', '/'));
        try
        {
            var stdoutTask = PumpReaderAsync(entry.Stream.StandardOutput, name);
            var stderrTask = PumpReaderAsync(entry.Stream.StandardError, name);
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch
        {
            // 程序被 Kill 時 reader 可能拋出，屬預期
        }

        bool wasStopped;
        lock (_lock)
        {
            wasStopped = entry.StoppedByUs || _disposed;
            if (_entries.TryGetValue(workingDirectory, out var current) && ReferenceEquals(current, entry))
                _entries.Remove(workingDirectory);
        }

        int exitCode = -1;
        try { exitCode = entry.Stream.ExitCode; } catch { }
        entry.Stream.Dispose();

        if (!wasStopped)
            OnProcessExited?.Invoke(workingDirectory, exitCode);
    }

    private async Task PumpReaderAsync(StreamReader reader, string name)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length > 0)
                OnOutput?.Invoke($"[watch:{name}] {line}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearAll();
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 2: App.xaml.cs 註冊 DI**

在 `services.AddSingleton<WatchRebuildService>();`（或相鄰服務註冊行）之後加：

```csharp
        services.AddSingleton<ComposeWatchService>();
```

- [ ] **Step 3: 全量驗證＋commit**

```bash
dotnet build src/DockerDashboard --no-incremental -v minimal
dotnet test tests/DockerDashboard.Tests -v minimal
git add -A
git commit -m "feat: 新增 ComposeWatchService 管理 watch 子程序"
```

---

### Task 5: MainViewModel 路由整合（雙 watch 機制）

**Files:**
- Modify: `src/DockerDashboard/ViewModels/MainViewModel.cs`（ctor 注入、`RestoreWatchStateFromSettings`、`Dispose`）
- Modify: `src/DockerDashboard/ViewModels/MainViewModel.DockerOps.cs`（`ToggleWatchServiceAsync`、`RescanProjectsAsync` 的 `_watchService.ClearAll()` 處）

**Interfaces:**
- Consumes: `ComposeWatchService`（Task 4）、`DockerCliService.IsWslUncPath`（Task 1）
- Produces: `private void UpdateComposeWatchForDirectory(string workingDirectory)`（MainViewModel 內部）
- 無單元測試：MainViewModel 無測試基礎建設（依賴 WPF Dispatcher），Task 6 手動冒煙涵蓋

- [ ] **Step 1: ctor 注入與事件接線**

`MainViewModel.cs`：欄位區加 `private readonly ComposeWatchService _composeWatch;`，建構函式參數列加 `ComposeWatchService composeWatch`（放在 `WatchRebuildService watchService` 之後），指派 `_composeWatch = composeWatch;`，並在 `_watchService.SetRebuildCallback(...)` 之後加：

```csharp
        _composeWatch.OnOutput = AppendLog;
        _composeWatch.OnProcessExited = OnComposeWatchExited;
```

新增私有方法（放在 `OnAutoRebuildTriggeredAsync` 附近）：

```csharp
    private void OnComposeWatchExited(string workingDirectory, int exitCode)
    {
        _ = Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ compose watch 異常退出 (exit code: {exitCode})：{workingDirectory}，已關閉該專案的 Auto Watch");
            foreach (var project in Projects)
                foreach (var compose in project.ComposeFiles)
                    foreach (var service in compose.Services)
                        if (string.Equals(service.WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase))
                            service.IsWatching = false;
            await SaveSettingsAsync();
        });
    }
```

注意：`MainWindow` 建構 `MainViewModel` 走 DI（`App.xaml.cs` Singleton），新增建構參數不需改呼叫端。

- [ ] **Step 2: ToggleWatchServiceAsync 路由**

`MainViewModel.DockerOps.cs` 將 `ToggleWatchServiceAsync` 改為：

```csharp
    [RelayCommand]
    private async Task ToggleWatchServiceAsync(DockerService? service)
    {
        if (service == null) return;

        service.IsWatching = !service.IsWatching;

        if (DockerCliService.IsWslUncPath(service.WorkingDirectory))
        {
            UpdateComposeWatchForDirectory(service.WorkingDirectory);
        }
        else if (service.IsWatching)
        {
            _watchService.AddWatch(service.WorkingDirectory, service.Name);
        }
        else
        {
            _watchService.RemoveWatch(service.WorkingDirectory, service.Name);
        }

        await SaveSettingsAsync();
    }
```

同檔案加私有方法（`ToggleWatchServiceAsync` 之後）：

```csharp
    private void UpdateComposeWatchForDirectory(string workingDirectory)
    {
        var watched = Projects
            .SelectMany(p => p.ComposeFiles)
            .SelectMany(c => c.Services)
            .Where(s => s.IsWatching &&
                        string.Equals(s.WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _composeWatch.SetWatchedServices(workingDirectory, watched);
    }
```

- [ ] **Step 3: RestoreWatchStateFromSettings 路由**

`MainViewModel.cs` 將 `RestoreWatchStateFromSettings` 改為：

```csharp
    internal void RestoreWatchStateFromSettings(AppSettings settings)
    {
        var wslDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in Projects)
        {
            foreach (var compose in project.ComposeFiles)
            {
                foreach (var service in compose.Services)
                {
                    service.IsWatching = settings.WatchEnabledServiceKeys.Contains(service.WatchKey);
                    if (!service.IsWatching) continue;

                    if (DockerCliService.IsWslUncPath(service.WorkingDirectory))
                        wslDirs.Add(service.WorkingDirectory);
                    else
                        _watchService.AddWatch(service.WorkingDirectory, service.Name);
                }
            }
        }

        foreach (var dir in wslDirs)
            UpdateComposeWatchForDirectory(dir);
    }
```

（`UpdateComposeWatchForDirectory` 定義在 `MainViewModel.DockerOps.cs`，同一 partial class 可直接呼叫。）

- [ ] **Step 4: RescanProjectsAsync 與 Dispose 清理**

`MainViewModel.DockerOps.cs` 的 `RescanProjectsAsync` 中 `_watchService.ClearAll();` 之後加：

```csharp
        _composeWatch.ClearAll();
```

`MainViewModel.cs` 的 `Dispose()` 中 `_watchService.Dispose();` 之後加：

```csharp
        _composeWatch.Dispose();
```

- [ ] **Step 5: 全量驗證＋commit**

```bash
dotnet build src/DockerDashboard --no-incremental -v minimal
dotnet test tests/DockerDashboard.Tests -v minimal
git add -A
git commit -m "feat: WSL 專案 Auto Watch 改走 compose watch"
```

---

### Task 6: 最終驗證與手動冒煙清單

**Files:** 無新增（驗證性 task）

- [ ] **Step 1: 全量 build＋test**

```bash
dotnet build src/DockerDashboard --no-incremental -v minimal   # 0 errors
dotnet test tests/DockerDashboard.Tests -v minimal              # 全過（原 7 + 新 17）
```

- [ ] **Step 2: 手動冒煙清單（回報使用者執行，WPF UI 無自動化）**

依 spec「測試與驗收」節：

1. Windows 軌回歸：現有 `D:\...` 專案 watch toggle 開關、存檔觸發自動重建 — 行為不變
2. WSL 軌（需先完成 spec C 節系統設定＋B3 的 `develop.watch`）：
   - `\\wsl$\<distro>\...` 匯入專案：清單、git 徽章、重建重啟正常
   - watch toggle 開啟後改 `.cs` 存檔：自動重建重啟該服務
   - 改 `appsettings.json`：同步重啟不重建
   - 關閉工具：工作管理員確認無殘留 `docker compose watch` 程序
   - compose 檔無 `develop.watch` 時開 toggle：日誌顯示錯誤、toggle 自動關閉

- [ ] **Step 3: 依「Git Push 強制規則」跑審查分級後推送、建 PR**

---

## CMP repo 配套與系統設定（不在本計畫實作範圍）

見 spec `docs/superpowers/specs/2026-07-08-wsl-windows-build-perf-design.md` B 節（`.dockerignore` 負向行、NuGet cache mount、`develop.watch` 區段 — 另開 session 到 CMP repo）與 C 節（Defender 排除、WSL Integration、clone 進 WSL — 使用者手動 checklist）。
