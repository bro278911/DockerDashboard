# 啟動效能優化與流程清理 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修復 App 啟動慢（Docker 檢查逾時、compose 掃描無快取）、讓「完整重建」真正重新拉取 base image、修正取消按鈕失效等怪流程、清除死碼。

**Architecture:** 不動整體 MVVM 結構。新增一個 `ScanCacheService`（JSON 檔快取掃描結果），其餘皆為既有檔案的外科手術式修改。新增 xUnit 測試專案覆蓋純邏輯部分。

**Tech Stack:** .NET 10 WPF、CommunityToolkit.Mvvm、xUnit（新增，僅測試專案）

## Global Constraints

- 目標框架 `net10.0-windows`，主專案為 `src/DockerDashboard/DockerDashboard.csproj`（無 solution 檔）
- 建置驗證指令：`dotnet build src/DockerDashboard`，必須 0 errors
- 測試指令：`dotnet test tests/DockerDashboard.Tests`（Task 3 建立後）
- 程式碼註解使用繁體中文，且只在 WHY 不明顯時寫
- Conventional Commits：`feat:` / `fix:` / `refactor:` / `chore:` / `test:`，subject ≤ 50 字元
- 所有 UI 更新必須經 `Application.Current?.Dispatcher.InvokeAsync`
- 不新增任何執行期 NuGet 依賴（xunit 僅限測試專案）
- 禁止在 main 分支直接實作 — 開工前建 `feat/startup-perf-and-flow-cleanup` 分支

---

### Task 0: 建立 feature branch

**Files:** 無

- [ ] **Step 1: 建分支**

```bash
git checkout -b feat/startup-perf-and-flow-cleanup
```

Run: `git branch --show-current`
Expected: `feat/startup-perf-and-flow-cleanup`

---

### Task 1: 移除 IDockerCliService 死碼（9 個零呼叫者方法）

**Files:**
- Modify: `src/DockerDashboard/Services/IDockerCliService.cs`
- Modify: `src/DockerDashboard/Services/DockerCliService.cs`

**Interfaces:**
- Consumes: 無
- Produces: 精簡後的 `IDockerCliService`（後續 Task 均以此為準）

**背景:** 以下 9 個方法在整個 codebase 中除了介面宣告與實作外零呼叫（已用 grep 驗證）：
`ComposeUpAsync`、`ComposeDownAsync`、`ComposeRestartAsync`、`ComposeStopAsync`、`ComposeStartAsync`、`ComposePullAsync`（非 WithLog 版）、`ComposeUpWithLogAsync`、`ComposeStartWithLogAsync`、`DockerNetworkPruneAsync`。
注意：`MainViewModel.DockerOps.cs` 裡的 `ComposeUpAsync`/`ComposeDownAsync`/`ComposePullAsync` 是 ViewModel 自己的 RelayCommand 方法，同名但無關，**不可動**。

- [ ] **Step 1: 刪除介面宣告**

從 `IDockerCliService.cs` 刪除這 9 個方法宣告（介面第 18-34、42-44、60-62、79-81 行附近）：

```csharp
// 刪除以下宣告：
Task<(int ExitCode, string Output)> ComposeUpAsync(
    string workingDirectory, string? serviceName = null, CancellationToken ct = default);
Task<(int ExitCode, string Output)> ComposeDownAsync(
    string workingDirectory, CancellationToken ct = default);
Task<(int ExitCode, string Output)> ComposeRestartAsync(
    string workingDirectory, string? serviceName = null, CancellationToken ct = default);
Task<(int ExitCode, string Output)> ComposeStopAsync(
    string workingDirectory, string? serviceName = null, CancellationToken ct = default);
Task<(int ExitCode, string Output)> ComposeStartAsync(
    string workingDirectory, string? serviceName = null, CancellationToken ct = default);
Task<(int ExitCode, string Output)> ComposePullAsync(
    string workingDirectory, string? serviceName = null, CancellationToken ct = default);
Task<(int ExitCode, string Output)> ComposeUpWithLogAsync(
    string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default);
Task<(int ExitCode, string Output)> ComposeStartWithLogAsync(
    string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default);
Task<(int ExitCode, string Output)> DockerNetworkPruneAsync(
    Action<string> onOutput, CancellationToken ct = default);
```

- [ ] **Step 2: 刪除 DockerCliService 對應實作**

刪除 `DockerCliService.cs` 中同名 9 個方法實作（約第 107-152、252-260、316-324、336-344、384-386 行）。

- [ ] **Step 3: 建置驗證**

Run: `dotnet build src/DockerDashboard`
Expected: Build succeeded, 0 errors（若有呼叫者漏網會在此爆 CS 錯誤，表示 grep 判斷錯誤，須還原該方法）

- [ ] **Step 4: Commit**

```bash
git add src/DockerDashboard/Services/IDockerCliService.cs src/DockerDashboard/Services/DockerCliService.cs
git commit -m "refactor: 移除 IDockerCliService 九個未使用方法"
```

---

### Task 2: Docker 可用性檢查加 10 秒逾時（啟動不再卡 3 分鐘）

**Files:**
- Modify: `src/DockerDashboard/Services/DockerCliService.cs`

**Interfaces:**
- Consumes: 無
- Produces: `RunCommandAsync` 新增 `TimeSpan? timeout` 參數；`IsDockerAvailableAsync` 行為不變但最長 10 秒回應

**背景:** `RunCommandAsync` 寫死 3 分鐘逾時。Docker Desktop 未就緒時 `docker version` 會掛住，`InitializeAsync` 的可用性檢查跟著卡住，這是 App 啟動慢的主因之一。WSL2 模式的 8 次重試迴圈每次也可能各卡 3 分鐘。

- [ ] **Step 1: RunCommandAsync 加 timeout 參數**

`DockerCliService.cs` 中：

```csharp
private async Task<(int ExitCode, string Output)> RunCommandAsync(
    string command, IEnumerable<string> args, string? workingDirectory,
    CancellationToken ct, TimeSpan? timeout = null)
{
    var psi = CreatePsi(command, args, workingDirectory);

    using var process = new Process { StartInfo = psi };
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(timeout ?? TimeSpan.FromMinutes(3));
    // ... 其餘不變
```

- [ ] **Step 2: IsDockerAvailableAsync 帶 10 秒逾時**

```csharp
public async Task<bool> IsDockerAvailableAsync(CancellationToken ct = default)
{
    try
    {
        // daemon 未就緒時 docker version 會掛住，10 秒足以涵蓋正常回應
        var (exitCode, _) = await RunCommandAsync(
            "docker", ["version", "--format", "json"], null, ct, TimeSpan.FromSeconds(10));
        return exitCode == 0;
    }
    catch
    {
        return false;
    }
}
```

- [ ] **Step 3: 建置驗證**

Run: `dotnet build src/DockerDashboard`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: 手動驗證（有 Docker 環境時）**

停止 Docker Desktop 後 `dotnet run --project src/DockerDashboard`。
Expected: 10~12 秒內出現「Docker 連線失敗」對話框，而非卡住數分鐘。

- [ ] **Step 5: Commit**

```bash
git add src/DockerDashboard/Services/DockerCliService.cs
git commit -m "fix: Docker 可用性檢查逾時降為 10 秒"
```

---

### Task 3: 掃描結果快取（測試專案 + ScanCacheService）

**Files:**
- Create: `tests/DockerDashboard.Tests/DockerDashboard.Tests.csproj`
- Create: `tests/DockerDashboard.Tests/ScanCacheServiceTests.cs`
- Create: `src/DockerDashboard/Services/ScanCacheService.cs`
- Modify: `src/DockerDashboard/Services/ComposeFileHelper.cs`（新增 `GetComposeFilePaths`）

**Interfaces:**
- Consumes: `ComposeFile` / `DockerService` models（既有）
- Produces:
  - `ScanCacheService.GetStamps(string directory)` → `List<CachedFileStamp>`（static）
  - `ScanCacheService.TryGet(string directory, List<CachedFileStamp> currentStamps)` → `ComposeFile?`
  - `ScanCacheService.Store(string directory, List<CachedFileStamp> stamps, ComposeFile composeFile)` → `void`
  - `ScanCacheService.LoadAsync()` / `SaveAsync()` → `Task`
  - `ComposeFileHelper.GetComposeFilePaths(string directory)` → `List<string>`（完整路徑）

**背景:** 每次啟動對每個 compose 目錄 spawn `docker compose config --format json`（各 0.5~2 秒，WSL2 更慢）。compose 檔極少變動 — 以檔案 mtime 驗證的 JSON 快取可讓熱啟動完全跳過 config 解析。這是啟動速度最大的優化。

- [ ] **Step 1: 建立測試專案**

```bash
dotnet new xunit -o tests/DockerDashboard.Tests
dotnet add tests/DockerDashboard.Tests reference src/DockerDashboard
```

編輯 `tests/DockerDashboard.Tests/DockerDashboard.Tests.csproj`，將 `<TargetFramework>` 改為 `net10.0-windows` 並加入 `<UseWPF>true</UseWPF>`（引用 WPF 主專案必需）：

```xml
<PropertyGroup>
  <TargetFramework>net10.0-windows</TargetFramework>
  <UseWPF>true</UseWPF>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <IsPackable>false</IsPackable>
</PropertyGroup>
```

刪掉範本產生的 `UnitTest1.cs`。

Run: `dotnet build tests/DockerDashboard.Tests`
Expected: Build succeeded

- [ ] **Step 2: 寫失敗測試**

`tests/DockerDashboard.Tests/ScanCacheServiceTests.cs`：

```csharp
using DockerDashboard.Models;
using DockerDashboard.Services;

namespace DockerDashboard.Tests;

public sealed class ScanCacheServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ScanCacheServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scancache-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static ComposeFile MakeComposeFile(string dir)
    {
        var compose = new ComposeFile
        {
            FileName = "docker-compose.yml",
            FilePath = Path.Combine(dir, "docker-compose.yml"),
            DirectoryPath = dir
        };
        compose.Services.Add(new DockerService
        {
            Name = "web",
            Image = "nginx",
            ContainerName = "my-web",
            Ports = "8080:80",
            ComposeFilePath = compose.FilePath,
            WorkingDirectory = dir
        });
        return compose;
    }

    private List<CachedFileStamp> WriteComposeAndStamp()
    {
        var path = Path.Combine(_tempDir, "docker-compose.yml");
        File.WriteAllText(path, "services:\n  web:\n    image: nginx\n");
        return ScanCacheService.GetStamps(_tempDir);
    }

    [Fact]
    public void TryGet_ReturnsNull_WhenNoEntry()
    {
        var cache = new ScanCacheService(Path.Combine(_tempDir, "cache.json"));
        var stamps = WriteComposeAndStamp();

        Assert.Null(cache.TryGet(_tempDir, stamps));
    }

    [Fact]
    public void TryGet_ReturnsComposeFile_WhenStampsMatch()
    {
        var cache = new ScanCacheService(Path.Combine(_tempDir, "cache.json"));
        var stamps = WriteComposeAndStamp();
        cache.Store(_tempDir, stamps, MakeComposeFile(_tempDir));

        var hit = cache.TryGet(_tempDir, stamps);

        Assert.NotNull(hit);
        var svc = Assert.Single(hit.Services);
        Assert.Equal("web", svc.Name);
        Assert.Equal("my-web", svc.ContainerName);
        Assert.Equal("8080:80", svc.Ports);
        Assert.Equal(_tempDir, svc.WorkingDirectory);
    }

    [Fact]
    public void TryGet_ReturnsNull_WhenFileChanged()
    {
        var cache = new ScanCacheService(Path.Combine(_tempDir, "cache.json"));
        var stamps = WriteComposeAndStamp();
        cache.Store(_tempDir, stamps, MakeComposeFile(_tempDir));

        var changed = stamps
            .Select(s => s with { MTimeUtcTicks = s.MTimeUtcTicks + 1 })
            .ToList();

        Assert.Null(cache.TryGet(_tempDir, changed));
    }

    [Fact]
    public void TryGet_ReturnsNull_WhenFileSetDiffers()
    {
        var cache = new ScanCacheService(Path.Combine(_tempDir, "cache.json"));
        var stamps = WriteComposeAndStamp();
        cache.Store(_tempDir, stamps, MakeComposeFile(_tempDir));

        var extra = new List<CachedFileStamp>(stamps)
        {
            new(Path.Combine(_tempDir, "docker-compose.override.yml"), 123)
        };

        Assert.Null(cache.TryGet(_tempDir, extra));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var cachePath = Path.Combine(_tempDir, "cache.json");
        var stamps = WriteComposeAndStamp();

        var writer = new ScanCacheService(cachePath);
        writer.Store(_tempDir, stamps, MakeComposeFile(_tempDir));
        await writer.SaveAsync();

        var reader = new ScanCacheService(cachePath);
        await reader.LoadAsync();

        Assert.NotNull(reader.TryGet(_tempDir, stamps));
    }

    [Fact]
    public void GetStamps_ReturnsMainAndOverrideFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "docker-compose.yml"), "services: {}");
        File.WriteAllText(Path.Combine(_tempDir, "docker-compose.override.yml"), "services: {}");

        var stamps = ScanCacheService.GetStamps(_tempDir);

        Assert.Equal(2, stamps.Count);
        Assert.All(stamps, s => Assert.True(s.MTimeUtcTicks > 0));
    }
}
```

- [ ] **Step 3: 跑測試確認失敗**

Run: `dotnet test tests/DockerDashboard.Tests`
Expected: FAIL — `ScanCacheService` / `CachedFileStamp` 不存在（CS0246 編譯錯誤，等同 RED）

- [ ] **Step 4: 實作 ComposeFileHelper.GetComposeFilePaths**

在 `ComposeFileHelper.cs` 新增（`BuildArgs` 已有同樣的檔名判斷邏輯，抽出共用）：

```csharp
public static List<string> GetComposeFilePaths(string directory)
{
    var paths = new List<string>();
    var mainFile = System.Array.Find(MainFiles, f => File.Exists(Path.Combine(directory, f)));
    if (mainFile == null) return paths;

    paths.Add(Path.Combine(directory, mainFile));

    var overrideFile = System.Array.Find(OverrideFiles, f => File.Exists(Path.Combine(directory, f)));
    if (overrideFile != null)
        paths.Add(Path.Combine(directory, overrideFile));

    var buildFile = System.Array.Find(BuildFiles, f => File.Exists(Path.Combine(directory, f)));
    if (buildFile != null)
        paths.Add(Path.Combine(directory, buildFile));

    return paths;
}
```

- [ ] **Step 5: 實作 ScanCacheService**

Create `src/DockerDashboard/Services/ScanCacheService.cs`：

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DockerDashboard.Models;

namespace DockerDashboard.Services;

public sealed record CachedFileStamp(string Path, long MTimeUtcTicks);

public sealed record CachedServiceDto(string Name, string Image, string ContainerName, string Ports);

public sealed record CachedDirectoryDto(
    List<CachedFileStamp> Files,
    string FileName,
    string FilePath,
    List<CachedServiceDto> Services);

// 以 compose 檔 mtime 驗證的掃描結果快取；命中時可跳過 docker compose config 解析
public sealed class ScanCacheService
{
    private static readonly string DefaultCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DockerDashboard",
        "scan-cache.json");

    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, CachedDirectoryDto> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public ScanCacheService() : this(DefaultCachePath) { }

    public ScanCacheService(string cachePath) => _cachePath = cachePath;

    public static List<CachedFileStamp> GetStamps(string directory)
    {
        return ComposeFileHelper.GetComposeFilePaths(directory)
            .Select(p => new CachedFileStamp(p, File.GetLastWriteTimeUtc(p).Ticks))
            .ToList();
    }

    public ComposeFile? TryGet(string directory, List<CachedFileStamp> currentStamps)
    {
        if (!_entries.TryGetValue(directory, out var entry))
            return null;

        if (entry.Files.Count != currentStamps.Count)
            return null;

        var cached = entry.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var current = currentStamps.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < cached.Count; i++)
        {
            if (!string.Equals(cached[i].Path, current[i].Path, StringComparison.OrdinalIgnoreCase) ||
                cached[i].MTimeUtcTicks != current[i].MTimeUtcTicks)
                return null;
        }

        var compose = new ComposeFile
        {
            FileName = entry.FileName,
            FilePath = entry.FilePath,
            DirectoryPath = directory
        };
        foreach (var svc in entry.Services)
        {
            compose.Services.Add(new DockerService
            {
                Name = svc.Name,
                Image = svc.Image,
                ContainerName = svc.ContainerName,
                Ports = svc.Ports,
                ComposeFilePath = entry.FilePath,
                WorkingDirectory = directory
            });
        }
        return compose;
    }

    public void Store(string directory, List<CachedFileStamp> stamps, ComposeFile composeFile)
    {
        _entries[directory] = new CachedDirectoryDto(
            stamps,
            composeFile.FileName,
            composeFile.FilePath,
            composeFile.Services
                .Select(s => new CachedServiceDto(s.Name, s.Image, s.ContainerName, s.Ports))
                .ToList());
    }

    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            if (!File.Exists(_cachePath)) return;
            var json = await File.ReadAllTextAsync(_cachePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, CachedDirectoryDto>>(json);
            if (data == null) return;
            foreach (var (key, value) in data)
                _entries[key] = value;
        }
        catch
        {
            // 快取損毀時直接忽略，掃描會重建
            _entries.Clear();
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var json = JsonSerializer.Serialize(
                _entries.ToDictionary(kv => kv.Key, kv => kv.Value));
            await File.WriteAllTextAsync(_cachePath, json);
        }
        catch
        {
            // 寫入失敗只損失快取效益，不影響功能
        }
    }
}
```

- [ ] **Step 6: 跑測試確認通過**

Run: `dotnet test tests/DockerDashboard.Tests`
Expected: PASS，6 tests, 0 failed

- [ ] **Step 7: Commit**

```bash
git add tests/DockerDashboard.Tests src/DockerDashboard/Services/ScanCacheService.cs src/DockerDashboard/Services/ComposeFileHelper.cs
git commit -m "feat: 新增掃描結果快取服務與測試專案"
```

---

### Task 4: Scanner 接上快取 + 平行解析 + 移除 ClearCache 競態

**Files:**
- Modify: `src/DockerDashboard/Services/ComposeFileScanner.cs`
- Modify: `src/DockerDashboard/App.xaml.cs`
- Modify: `src/DockerDashboard/ViewModels/MainViewModel.DockerOps.cs:788-823`（RescanProjectsAsync 傳 `useCache: false`）

**Interfaces:**
- Consumes: Task 3 的 `ScanCacheService`
- Produces: `ComposeFileScanner.ScanFolderAsync(string folderPath, bool useCache = true)` — 簽名新增參數；建構子改為 `ComposeFileScanner(ScanCacheService cache)`

**背景:** 三件事一起做（都在同一個方法裡）：
1. 快取命中就跳過 `docker compose config` spawn
2. 目錄間平行解析（`SemaphoreSlim(4)`），目前是序列
3. 刪掉 `ComposeFileHelper.ClearCache()` — `InitializeAsync` 用 `Task.WhenAll` 平行掃多個資料夾時，每個掃描都清掉全域快取，互相踩踏；該快取本身已用目錄 mtime 驗證，清除毫無必要

- [ ] **Step 1: 改寫 ScanFolderAsync**

```csharp
private readonly ScanCacheService _cache;

public ComposeFileScanner(ScanCacheService cache) => _cache = cache;

public async Task<List<ComposeFile>> ScanFolderAsync(string folderPath, bool useCache = true)
{
    if (!Directory.Exists(folderPath))
        return [];

    await _cache.LoadAsync();

    var directories = EnumerateComposeDirectories(folderPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(d => FindMainComposeFile(d) != null)
        .ToList();

    // docker compose config 每次 spawn 0.5~2 秒，目錄間平行化
    using var semaphore = new SemaphoreSlim(4);
    var results = await Task.WhenAll(directories.Select(async directory =>
    {
        var stamps = ScanCacheService.GetStamps(directory);
        if (useCache && _cache.TryGet(directory, stamps) is { } cached)
            return cached;

        await semaphore.WaitAsync();
        try
        {
            var mainComposePath = FindMainComposeFile(directory)!;
            var composeFile = await ParseWithDockerCliAsync(mainComposePath) ?? ParseManually(mainComposePath);
            if (composeFile != null)
                _cache.Store(directory, stamps, composeFile);
            return composeFile;
        }
        finally
        {
            semaphore.Release();
        }
    }));

    await _cache.SaveAsync();
    return results.OfType<ComposeFile>().ToList();
}
```

刪除原方法開頭的 `ComposeFileHelper.ClearCache();` 與 `seenDirectories` 邏輯（`Distinct` 已取代）。

- [ ] **Step 2: DI 註冊**

`App.xaml.cs` 的 `ConfigureServices` 加一行（放在 `ComposeFileScanner` 之前）：

```csharp
services.AddSingleton<ScanCacheService>();
```

（`ComposeFileScanner` 已是 singleton 註冊，建構子注入自動生效。）

- [ ] **Step 3: RescanProjectsAsync 強制略過快取**

`MainViewModel.DockerOps.cs` `RescanProjectsAsync` 中：

```csharp
var composeFiles = await _scanner.ScanFolderAsync(folder, useCache: false);
```

（「重新掃描」按鈕的語意就是強制重掃，必須繞過快取。）

- [ ] **Step 4: 建置 + 測試**

Run: `dotnet build src/DockerDashboard && dotnet test tests/DockerDashboard.Tests`
Expected: Build succeeded；6 tests passed

- [ ] **Step 5: 手動驗證**

`dotnet run --project src/DockerDashboard` 啟動兩次：
- 第一次啟動後確認 `%APPDATA%\DockerDashboard\scan-cache.json` 已產生
- 第二次啟動應明顯變快（專案樹幾乎立即出現），服務清單、port 與第一次一致
- 按「重新掃描」仍能正確重建清單

- [ ] **Step 6: Commit**

```bash
git add src/DockerDashboard/Services/ComposeFileScanner.cs src/DockerDashboard/App.xaml.cs src/DockerDashboard/ViewModels/MainViewModel.DockerOps.cs
git commit -m "feat: 掃描接快取並平行解析 compose 目錄"
```

---

### Task 5: Docker 未連線時仍載入專案清單

**Files:**
- Modify: `src/DockerDashboard/ViewModels/MainViewModel.cs:124-198`（InitializeAsync）

**Interfaces:**
- Consumes: Task 4 的快取掃描（Docker 不可用時快取/YAML fallback 仍能出清單）
- Produces: 無新介面

**背景:** 目前 Docker 連不上就 `return`，專案清單完全不載入 — 但掃描器本來就有 YAML fallback、現在又有快取，清單顯示不依賴 daemon。使用者打開 App 至少要能看到自己的專案。

- [ ] **Step 1: 移除 early return**

`InitializeAsync` 中，把：

```csharp
if (!IsDockerAvailable)
{
    StatusMessage = "⚠ Docker 未啟動或未安裝（請檢查設定）";
    return;
}
```

改成不 return，只留警告（放在方法尾端 `StatusMessage = "就緒"` 的位置做條件切換）：

```csharp
// Docker 不可用仍繼續：掃描有快取與 YAML fallback，清單不依賴 daemon
```

並將尾端：

```csharp
StatusMessage = "就緒";
```

改為：

```csharp
StatusMessage = IsDockerAvailable ? "就緒" : "⚠ Docker 未連線（清單為快取資料，狀態監控暫停）";
```

`_monitor.Start(...)` 保持照常呼叫 — 輪詢的 `docker ps` 失敗時本來就靜默回空清單，等 Docker 起來後自動恢復狀態顯示，等同免費的重連機制。

- [ ] **Step 2: 建置驗證**

Run: `dotnet build src/DockerDashboard`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: 手動驗證**

停止 Docker Desktop → 啟動 App → 對話框選「否」（不切模式）。
Expected: 專案樹仍列出所有專案與服務（狀態全灰/停止）；狀態列顯示 Docker 未連線警告。啟動 Docker Desktop 後 5 秒內（輪詢間隔）狀態自動變綠。

- [ ] **Step 4: Commit**

```bash
git add src/DockerDashboard/ViewModels/MainViewModel.cs
git commit -m "fix: Docker 未連線時仍載入專案清單"
```

---

### Task 6: RescanProjects 保留 git 資訊 + 警告改寫入日誌

**Files:**
- Modify: `src/DockerDashboard/ViewModels/MainViewModel.DockerOps.cs:787-823`（RescanProjectsAsync）
- Modify: `src/DockerDashboard/ViewModels/MainViewModel.cs:180-185`（InitializeAsync 的警告）

**Interfaces:**
- Consumes: 既有 `BuildProjectAsync(string folderPath)`（`MainViewModel.cs:280`，回傳含 git 資訊的 `DockerProject`）
- Produces: 無新介面

**背景:** 兩個怪流程：
1. `RescanProjectsAsync` 自己 new `DockerProject`、不走 `BuildProjectAsync` → 重新掃描後 `IsGitRepo`/`CurrentBranch`/`IsDirty` 全部遺失，UI 上 git 分支徽章消失
2. 「未偵測到服務」警告塞進 `StatusMessage`，但迴圈結束立刻被「重新掃描完成」/「就緒」蓋掉 — 使用者永遠看不到

- [ ] **Step 1: 改寫 RescanProjectsAsync**

```csharp
[RelayCommand]
private async Task RescanProjectsAsync()
{
    var folders = Projects.Select(p => p.FolderPath).ToList();
    Projects.Clear();

    var results = await Task.WhenAll(
        folders.Where(Directory.Exists).Select(BuildProjectAsync));

    foreach (var project in results)
    {
        Projects.Add(project);
        if (project.ComposeFiles.Count == 0)
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ {project.Name} 未偵測到服務（docker compose config 可能失敗）");
    }

    _watchService.ClearAll();
    var settings = await _settingsService.LoadAsync();
    RestoreWatchStateFromSettings(settings);

    await _monitor.ForceRefreshAsync();
    StatusMessage = "重新掃描完成";
}
```

注意：`BuildProjectAsync` 內呼叫 `_scanner.ScanFolderAsync(folderPath)`，重新掃描需繞過快取 — 將 `BuildProjectAsync` 加參數：

```csharp
private async Task<DockerProject> BuildProjectAsync(string folderPath, bool useCache = true)
{
    // ...
    var composeFiles = await _scanner.ScanFolderAsync(folderPath, useCache);
    // ... 其餘（git 判斷等）不變
}
```

RescanProjectsAsync 呼叫處改為：

```csharp
folders.Where(Directory.Exists).Select(f => BuildProjectAsync(f, useCache: false))
```

（Task 4 Step 3 對 RescanProjectsAsync 的修改被本 Task 的改寫取代 — 執行到這裡時以本版為準。）

- [ ] **Step 2: InitializeAsync 的同款警告也改寫入日誌**

`MainViewModel.cs` `InitializeAsync` 中：

```csharp
foreach (var project in loaded.OfType<DockerProject>())
{
    Projects.Add(project);
    if (project.ComposeFiles.Count == 0)
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ {project.Name} 未偵測到服務（docker compose config 可能失敗）");
}
```

- [ ] **Step 3: 建置驗證**

Run: `dotnet build src/DockerDashboard`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: 手動驗證**

啟動 App → 確認某 git 專案顯示分支名 → 按「重新掃描」。
Expected: 掃描完成後分支徽章仍在；含無服務資料夾時，警告出現在日誌面板而非一閃而過。

- [ ] **Step 5: Commit**

```bash
git add src/DockerDashboard/ViewModels/MainViewModel.DockerOps.cs src/DockerDashboard/ViewModels/MainViewModel.cs
git commit -m "fix: 重新掃描保留 git 資訊並將警告寫入日誌"
```

---

### Task 7: 拉取映像可取消 + 有界平行 + 統一批次樣板

**Files:**
- Modify: `src/DockerDashboard/ViewModels/MainViewModel.DockerOps.cs:700-767`（ComposePullAsync、PullAllImagesAsync）

**Interfaces:**
- Consumes: 既有 `_dockerCli.ComposePullWithLogAsync(dir, onOutput, serviceName, ct)`（已支援 ct，只是沒被傳入）
- Produces: 無新介面

**背景:** 兩個 pull 命令設了 `IsOperating = true` 但不建 `_operationCts`、不傳 CancellationToken → 「取消」按鈕亮著，按下去只會把 UI 卡在「正在取消操作...」直到拉完，實際什麼都沒取消。`PullAllImagesAsync` 還是無上限平行（全部 compose 同時 pull，互搶頻寬），也不清日誌、不暫停監控 — 與其他批次操作全部不一致。

- [ ] **Step 1: 改寫 PullAllImagesAsync（套 AllUpAsync 同款樣板）**

```csharp
[RelayCommand]
private async Task PullAllImagesAsync()
{
    if (Projects.Count == 0)
    {
        StatusMessage = "⚠ 請先匯入專案資料夾";
        return;
    }

    var cts = new CancellationTokenSource();
    _operationCts?.Dispose();
    _operationCts = cts;
    var ct = cts.Token;

    IsOperating = true;
    IsCancelling = false;
    StatusMessage = "正在拉取所有映像...";
    LogLines.Clear();
    AppendLog($"[{DateTime.Now:HH:mm:ss}] ⬇ 拉取所有映像（並行）");

    var composeFiles = Projects.SelectMany(p => p.ComposeFiles).ToList();
    var errors = new ConcurrentBag<string>();
    var maxParallel = Math.Clamp(_batchStartupParallelism, 1, 8);
    using var semaphore = new SemaphoreSlim(maxParallel);

    var tasks = composeFiles.Select(async compose =>
    {
        try { await semaphore.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (ct.IsCancellationRequested) return;
            var (exitCode, _) = await _dockerCli.ComposePullWithLogAsync(
                compose.DirectoryPath, AppendLog, ct: ct);
            if (ct.IsCancellationRequested)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ {compose.FileName} 已取消");
            else if (exitCode != 0)
                errors.Add(compose.FileName);
            else
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 映像拉取完成");
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
        finally { semaphore.Release(); }
    });

    bool wasCancelled = false;
    try
    {
        await Task.WhenAll(tasks);
    }
    finally
    {
        wasCancelled = cts.IsCancellationRequested;
        if (ReferenceEquals(_operationCts, cts))
            _operationCts = null;
        cts.Dispose();
        IsCancelling = false;
        IsOperating = false;
    }

    if (wasCancelled)
        StatusMessage = "⏹ 操作已取消";
    else
        StatusMessage = errors.IsEmpty ? "✅ 所有映像已更新" : $"⚠ {errors.Count} 個映像拉取失敗";
}
```

（pull 不改容器狀態，不需要 `SuspendRefreshes`/`ForceRefreshAsync`。）

- [ ] **Step 2: 改寫單檔 ComposePullAsync（同款取消樣板）**

```csharp
[RelayCommand]
private async Task ComposePullAsync(ComposeFile? compose)
{
    if (compose == null) return;

    var cts = new CancellationTokenSource();
    _operationCts?.Dispose();
    _operationCts = cts;
    var ct = cts.Token;

    IsOperating = true;
    IsCancelling = false;
    StatusMessage = $"正在拉取 {compose.FileName} 映像...";
    AppendLog($"[{DateTime.Now:HH:mm:ss}] ⬇ 拉取映像 {compose.FileName}");

    bool wasCancelled = false;
    try
    {
        var (exitCode, _) = await _dockerCli.ComposePullWithLogAsync(compose.DirectoryPath, AppendLog, ct: ct);
        if (exitCode == 0)
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 映像拉取完成");
        else
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 映像拉取失敗");
        StatusMessage = exitCode == 0 ? $"✅ {compose.FileName} 映像已更新" : $"⚠ {compose.FileName} 映像拉取失敗";
    }
    catch (OperationCanceledException)
    {
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ {compose.FileName} 已取消");
    }
    catch (Exception ex)
    {
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 例外: {ex.Message}");
        StatusMessage = $"⚠ {compose.FileName} 映像拉取失敗";
    }
    finally
    {
        wasCancelled = cts.IsCancellationRequested;
        if (ReferenceEquals(_operationCts, cts))
            _operationCts = null;
        cts.Dispose();
        IsCancelling = false;
        IsOperating = false;
    }

    if (wasCancelled)
        StatusMessage = "⏹ 操作已取消";
}
```

- [ ] **Step 3: 建置驗證**

Run: `dotnet build src/DockerDashboard`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: 手動驗證**

按「拉取映像」→ 立刻按「取消」。
Expected: 數秒內日誌出現「⏹ … 已取消」、狀態列顯示「⏹ 操作已取消」，不再卡「正在取消操作...」。

- [ ] **Step 5: Commit**

```bash
git add src/DockerDashboard/ViewModels/MainViewModel.DockerOps.cs
git commit -m "fix: 拉取映像支援取消並限制平行數"
```

---

### Task 8: 完整重建加 --pull（重置＋重新拉取一鍵完成）

**Files:**
- Modify: `src/DockerDashboard/Services/DockerCliService.cs`（ComposeForceRebuildWithLogAsync）
- Modify: `src/DockerDashboard/MainWindow.xaml:66-75`（完整重建按鈕 tooltip）

**Interfaces:**
- Consumes: 無
- Produces: 無簽名變更，僅行為變更

**背景:** 「完整重建」目前跑 `build --no-cache` — 只砍本地建置快取，**不會**重新拉取 base image。使用者想「重置＋重新拉取」得先按「拉取映像」等一輪、再按「完整重建」再等一輪。加上 `--pull` 讓 build 過程順帶檢查並拉取較新的 base image，一鍵完成。

- [ ] **Step 1: build 加 --pull**

`DockerCliService.cs` `ComposeForceRebuildWithLogAsync`：

```csharp
var buildArgs = BuildComposeArgs(workingDirectory, ["build", "--no-cache", "--pull"]);
```

- [ ] **Step 2: 更新按鈕 tooltip**

`MainWindow.xaml` 完整重建按鈕：

```xml
ToolTip="強制重建所有 image（--no-cache --pull，重新拉取 base image 從頭建，耗時較長）"
```

- [ ] **Step 3: 建置驗證**

Run: `dotnet build src/DockerDashboard`
Expected: Build succeeded, 0 errors

- [ ] **Step 4: 手動驗證（有 Docker 環境時）**

對任一含 `build:` 的專案按「完整重建」。
Expected: 日誌出現 base image pull 檢查（如 `nginx Pulling` / `Pull complete` 或 layer digest 檢查行），完成後容器正常啟動。

- [ ] **Step 5: Commit**

```bash
git add src/DockerDashboard/Services/DockerCliService.cs src/DockerDashboard/MainWindow.xaml
git commit -m "feat: 完整重建加 --pull 重新拉取 base image"
```

---

### Task 9: DockerRepairWindow 關窗取消進行中操作

**Files:**
- Modify: `src/DockerDashboard/Views/DockerRepairWindow.xaml.cs`

**Interfaces:**
- Consumes: 無
- Produces: 無

**背景:** 只有「關閉」按鈕的 `Close_Click` 會 `_cts?.Cancel()`；點視窗右上角 X 直接關窗，prune 繼續在背景跑且無法再取消。

- [ ] **Step 1: 覆寫 OnClosed**

`DockerRepairWindow.xaml.cs` 加入：

```csharp
protected override void OnClosed(EventArgs e)
{
    _cts?.Cancel();
    base.OnClosed(e);
}
```

`Close_Click` 中的 `_cts?.Cancel();` 可移除（OnClosed 已涵蓋，`Close()` 必觸發）：

```csharp
private void Close_Click(object sender, RoutedEventArgs e)
{
    Close();
}
```

- [ ] **Step 2: 建置驗證**

Run: `dotnet build src/DockerDashboard`
Expected: Build succeeded, 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/DockerDashboard/Views/DockerRepairWindow.xaml.cs
git commit -m "fix: 修復視窗關閉時取消進行中清理"
```

---

### Task 10: 收尾驗證與推送閘門

**Files:** 無

- [ ] **Step 1: 全量建置與測試**

Run: `dotnet build src/DockerDashboard --no-incremental && dotnet test tests/DockerDashboard.Tests`
Expected: 0 errors；全部測試 passed

- [ ] **Step 2: 手動冒煙測試（一次跑完）**

`dotnet run --project src/DockerDashboard`：
1. 冷啟動（有 scan-cache.json）→ 專案樹立即出現
2. 「全部啟動」→ 全綠
3. 「拉取映像」→ 中途取消 → 狀態列「⏹ 操作已取消」
4. 「重新掃描」→ git 分支徽章仍在
5. 「全部停止」→ 全灰

- [ ] **Step 3: 依「Git Push 強制規則」走審查分級**

多檔 + ≥50 行 → 一般級距：`code-reviewer` agent 一層（Codex 開關依 session 狀態）。修完 🔴🟡 後：

```bash
git push -u origin feat/startup-perf-and-flow-cleanup
```

---

## 決策記錄（供審閱）

| 決策 | 理由 |
|---|---|
| 掃描快取用獨立 `scan-cache.json` 而非塞進 settings.json | settings 是使用者意圖、快取是衍生資料，損毀可整檔丟棄 |
| 完整重建/增量重建的 `SemaphoreSlim(2)` 維持不動 | 平行 no-cache build 會互搶 CPU/IO，加大只會更慢；瓶頸在 build 本身 |
| pull 平行數沿用 `StartupParallelism` 設定 | 不新增設定項（YAGNI），語意同為「批次操作平行度」 |
| Docker 不可用時 monitor 照常啟動 | 輪詢失敗靜默回空清單，daemon 恢復後自動重連，免寫重連邏輯 |
| MainViewModel 行為修改不寫單元測試 | 需先抽 `ComposeFileScanner` 介面才可注入假件，重構成本大於收益；以建置＋手動冒煙驗證 |
