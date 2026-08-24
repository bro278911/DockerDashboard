using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerDashboard.Models;
using DockerDashboard.Services;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;

namespace DockerDashboard.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDockerCliService _dockerCli;
    private readonly IGitService _gitService;
    private readonly ComposeFileScanner _scanner;
    private readonly SettingsService _settingsService;
    private readonly ContainerMonitorService _monitor;
    private readonly HostBuildService _hostBuild;
    private readonly FastDevReloadService _fastDevReload;
    private readonly UpdateService _updateService;
    private readonly NodeProcessService _nodeService;
    private Forms.NotifyIcon? _notifyIcon;
    private readonly ConcurrentQueue<string> _pendingLogQueue = new();
    private int _isLogFlushScheduled;
    private int _batchStartupParallelism = 3;
    // Docker 專案清單是否已載入完成，供 SaveSettingsAsync 判斷可否覆寫該清單
    private bool _dockerProjectsLoaded;
    private bool _dotnetSdkChecked;
    private bool _dotnetSdkAvailable;
    private CancellationTokenSource? _operationCts;

    public ObservableCollection<DockerProject> Projects { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<string> RecentlyRemovedFolders { get; } = [];

    private ICollectionView? _logView;
    public ICollectionView? LogView
    {
        get => _logView;
        private set => SetProperty(ref _logView, value);
    }

    [ObservableProperty]
    private string _logFilter = string.Empty;

    [ObservableProperty]
    private bool _fastDevAutoReloadEnabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    private DockerService? _selectedService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartButtonText))]
    private DockerProject? _selectedProject;

    [ObservableProperty]
    private ComposeFile? _selectedComposeFile;

    // 傳統操作模式開關（設定持久化）：false 只露 Fast Dev，true 才顯示舊操作
    [ObservableProperty]
    private bool _showClassicControls;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAllUp))]
    [NotifyPropertyChangedFor(nameof(CanAllDown))]
    [NotifyPropertyChangedFor(nameof(CanRebuild))]
    [NotifyPropertyChangedFor(nameof(CanCancelOperation))]
    [NotifyPropertyChangedFor(nameof(CanRemoveProject))]
    private bool _isOperating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancelOperation))]
    private bool _isCancelling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAllUp))]
    [NotifyPropertyChangedFor(nameof(CanAllDown))]
    private int _runningCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAllUp))]
    [NotifyPropertyChangedFor(nameof(CanAllDown))]
    [NotifyPropertyChangedFor(nameof(CanRebuild))]
    private int _totalCount;

    [ObservableProperty]
    private int _stoppedCount;

    public bool CanAllUp => !IsOperating && TotalCount > 0 && RunningCount < TotalCount;

    // 上方啟動鈕字：跟左側選取範圍走，讓「按下會起什麼」一眼看得出來，不會誤以為都是全部
    public string StartButtonText =>
        SelectedService != null ? $"啟動 {SelectedService.Name}"
        : SelectedProject != null ? $"啟動 {SelectedProject.Name}"
        : "全部啟動";
    public bool CanRebuild => !IsOperating && TotalCount > 0;
    public bool CanAllDown => !IsOperating && RunningCount > 0;
    public bool CanCancelOperation => IsOperating && !IsCancelling;
    public bool CanRemoveProject => !IsOperating;

    [ObservableProperty]
    private string _statusMessage = "就緒";

    [ObservableProperty]
    private bool _isDockerAvailable;

    [ObservableProperty]
    private string _dockerModeLabel = "Docker Desktop";

    public MainViewModel(
        IDockerCliService dockerCli,
        IGitService gitService,
        ComposeFileScanner scanner,
        SettingsService settingsService,
        ContainerMonitorService monitor,
        HostBuildService hostBuild,
        FastDevReloadService fastDevReload,
        UpdateService updateService,
        NodeProcessService nodeService)
    {
        _dockerCli = dockerCli;
        _gitService = gitService;
        _scanner = scanner;
        _settingsService = settingsService;
        _monitor = monitor;
        _hostBuild = hostBuild;
        _fastDevReload = fastDevReload;
        _updateService = updateService;
        _nodeService = nodeService;

        _scanner.Log = message => AppendLog($"[{DateTime.Now:HH:mm:ss}] {message}");

        _monitor.ContainersUpdated += OnContainersUpdated;
        _monitor.ContainerCrashed += OnContainerCrashed;
        _fastDevReload.OnSolutionChanged = OnFastDevSolutionChangedAsync;

        LogView = CollectionViewSource.GetDefaultView(LogLines);
        LogView.Filter = LogFilterPredicate;
    }

    public void SetNotifyIcon(Forms.NotifyIcon? icon)
    {
        _notifyIcon = icon;
    }

    public async Task InitializeAsync()
    {
        var settings = await _settingsService.LoadAsync();

        // 盡早載入：SaveSettingsAsync 會整包覆寫 settings.json，若視窗在下面 Docker 連線等待
        // 期間仍可互動、使用者恰好觸發存檔，載入太晚會把尚未還原的 FrontendProjects 存成空清單
        LoadFrontendProjects(settings);

        ApplyDockerModeSettings(settings);

        // WSL2 模式：[boot] 的 service docker start 可能需數秒才完成，
        // 最多等 12 秒讓 daemon 就緒再判斷失敗
        IsDockerAvailable = await TryConnectDockerAsync(settings.DockerMode);

        if (!IsDockerAvailable)
        {
            var currentMode = settings.DockerMode == DockerMode.Wsl2 ? "WSL2" : "Docker Desktop";
            var altMode = settings.DockerMode == DockerMode.Wsl2 ? "Docker Desktop" : "WSL2";

            var result = System.Windows.MessageBox.Show(
                $"目前模式「{currentMode}」無法連線到 Docker。\n\n" +
                $"是否要切換到「{altMode}」模式重試？\n\n" +
                "（可在「設定」中隨時切換模式）",
                "Docker 連線失敗",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                settings.DockerMode = settings.DockerMode == DockerMode.Wsl2
                    ? DockerMode.DockerDesktop
                    : DockerMode.Wsl2;
                await _settingsService.SaveAsync(settings);
                ApplyDockerModeSettings(settings);

                IsDockerAvailable = await TryConnectDockerAsync(settings.DockerMode);
            }

            // Docker 不可用仍繼續：掃描有快取與 YAML fallback，清單不依賴 daemon
        }

        foreach (var folder in settings.RecentlyRemovedFolders)
            RecentlyRemovedFolders.Add(folder);

        var loaded = await Task.WhenAll(
            settings.ImportedFolders
                .Where(System.IO.Directory.Exists)
                .Select(async folder =>
                {
                    try { return await BuildProjectAsync(folder); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Init] 載入 {folder} 失敗: {ex.Message}");
                        return null;
                    }
                }));

        foreach (var project in loaded.OfType<DockerProject>())
        {
            Projects.Add(project);
            if (project.ComposeFiles.Count == 0)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ {project.Name} 未偵測到服務（docker compose config 可能失敗）");
        }

        _dockerProjectsLoaded = true; // 此後 SaveSettingsAsync 才可覆寫 Docker 專案清單

        _monitor.Start(TimeSpan.FromSeconds(settings.PollIntervalSeconds));
        var statusConfirmed = await _monitor.ForceRefreshAsync();

        ApplySettings(settings);
        RestoreFastDevStateFromSettings(settings, statusConfirmed);

        StatusMessage = IsDockerAvailable ? "就緒" : "⚠ Docker 未連線（顯示快取清單，連線恢復後自動更新）";

        // 背景靜默檢查更新，不阻塞啟動
        if (settings.AutoCheckUpdate)
            _ = CheckUpdateAsync();

        // 背景暖機 dotnet SDK 檢查：按下全部啟動(Fast Dev)時就不必等 dotnet 冷啟動
        _ = IsDotnetSdkAvailableAsync();
    }

    private async Task<bool> TryConnectDockerAsync(DockerMode mode)
    {
        if (mode != DockerMode.Wsl2)
            return await _dockerCli.IsDockerAvailableAsync();

        // WSL2 mode: [boot] command 需要時間啟動 daemon，最多等 12 秒
        const int maxAttempts = 8;
        for (int i = 0; i < maxAttempts; i++)
        {
            if (i > 0)
            {
                StatusMessage = $"⏳ 等待 Docker daemon 就緒… ({i}/{maxAttempts - 1})";
                await Task.Delay(1500);
            }

            if (await _dockerCli.IsDockerAvailableAsync())
                return true;
        }

        return false;
    }

    internal void ApplySettings(AppSettings settings)
    {
        _batchStartupParallelism = Math.Clamp(settings.StartupParallelism, 1, 8);
        _fastDevReload.IsEnabled = settings.FastDevAutoReloadEnabled;
        FastDevAutoReloadEnabled = settings.FastDevAutoReloadEnabled;
        ShowClassicControls = settings.ClassicControlsEnabled;
    }

    internal void RestoreFastDevStateFromSettings(AppSettings settings, bool statusConfirmed)
    {
        // 殘留旗標會讓啟動鈕把服務誤判為「已在 Fast Dev」而整個跳過啟動，故只還原沒被確認停掉的服務。
        // statusConfirmed=false（docker 沒連上、refresh 被 suspend）時分不出「沒在跑」與「不知道」，
        // 照舊全還原 — 寧可留殘留旗標，也不把在跑的 Fast Dev 誤清（誤清連熱重載 watcher 都會一起消失）
        var staleCount = 0;
        foreach (var service in Projects.SelectMany(p => p.ComposeFiles).SelectMany(c => c.Services))
        {
            var persisted = settings.FastDevEnabledServiceKeys.Contains(service.WatchKey);
            var confirmedDown = statusConfirmed &&
                service.Status is ContainerStatus.Stopped or ContainerStatus.Exited or ContainerStatus.Dead;
            service.IsFastDev = persisted && !confirmedDown;
            if (persisted && confirmedDown) staleCount++;
        }
        if (staleCount > 0)
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 🧹 清除 {staleCount} 個殘留 Fast Dev 標記（容器未在執行）");

        foreach (var dir in Projects.SelectMany(p => p.ComposeFiles).SelectMany(c => c.Services)
                     .Where(s => s.IsFastDev)
                     .Select(s => s.WorkingDirectory)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            _fastDevReload.Watch(dir);
    }

    internal void ApplyDockerModeSettings(AppSettings settings)
    {
        _dockerCli.UseComposeV2 = settings.UseComposeV2;
        _dockerCli.DockerMode = settings.DockerMode;
        _dockerCli.WslDistroName = settings.WslDistroName;
        _dockerCli.BuildKitParallelism = settings.BuildKitParallelism;
        _scanner.DockerMode = settings.DockerMode;
        _scanner.WslDistroName = settings.WslDistroName;
        DockerModeLabel = settings.DockerMode == DockerMode.Wsl2
            ? $"WSL2 ({settings.WslDistroName})"
            : "Docker Desktop";
    }

    private async Task<DockerProject> BuildProjectAsync(string folderPath, bool useCache = true)
    {
        var project = new DockerProject
        {
            Name = System.IO.Path.GetFileName(folderPath),
            FolderPath = folderPath
        };

        var composeFiles = await _scanner.ScanFolderAsync(folderPath, useCache);
        foreach (var cf in composeFiles)
            project.ComposeFiles.Add(cf);

        if (_gitService.IsGitRepository(folderPath))
        {
            project.IsGitRepo = true;
            var branchTask = _gitService.GetCurrentBranchAsync(folderPath);
            var dirtyTask = _gitService.IsDirtyAsync(folderPath);
            await Task.WhenAll(branchTask, dirtyTask);
            project.CurrentBranch = branchTask.Result;
            project.IsDirty = dirtyTask.Result;
        }

        return project;
    }

    // 掃不到任何 compose 檔就不匯入，並回傳失敗原因：
    // 資料夾選取對話框「點進資料夾後才按選擇」會回傳子資料夾、或不存在的重複路徑（…\X\X），
    // 匯進來只會變成一個沒有服務的空專案，還會寫進設定，下次啟動才被 Directory.Exists 濾掉
    internal async Task<string?> AddProjectFromFolderAsync(string folderPath)
    {
        if (!System.IO.Directory.Exists(folderPath))
            return "資料夾不存在";

        var project = await BuildProjectAsync(folderPath);
        // 兩種情況都會是 0：真的沒有 compose 檔、或有但解析全失敗（後者掃描時已寫入操作紀錄）
        if (project.ComposeFiles.Count == 0)
            return "此資料夾及其子目錄找不到可用的 docker compose 檔（沒有 compose 檔，或 compose 解析失敗，詳見操作紀錄）";

        Projects.Add(project);
        return null;
    }

    internal void RemoveRecentFolder(string folder)
    {
        var existing = RecentlyRemovedFolders.FirstOrDefault(
            f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            RecentlyRemovedFolders.Remove(existing);
    }

    internal async Task SaveSettingsAsync()
    {
        var settings = await _settingsService.LoadAsync();

        // Docker 專案清單在 InitializeAsync 後段才填好（WSL2 連線最長等 12 秒 + 掃描），
        // 而前端 Tab 在這段期間已可操作。此時存檔會把還沒載入的清單覆寫成空的，
        // 故未載入完成前不動這兩個欄位（前端清單在 InitializeAsync 最前面就載入，不受影響）
        if (_dockerProjectsLoaded)
        {
            settings.ImportedFolders = [.. Projects.Select(p => p.FolderPath)];
            settings.RecentlyRemovedFolders = [.. RecentlyRemovedFolders];
        }

        settings.FrontendProjects =
            [.. InternalProjects.Concat(ExternalProjects).Select(p => p.ToConfig())];
        await _settingsService.SaveAsync(settings);
    }

    private void UpdateCounts()
    {
        var allServices = Projects
            .SelectMany(p => p.ComposeFiles)
            .SelectMany(c => c.Services)
            .ToList();

        TotalCount = allServices.Count;
        RunningCount = allServices.Count(s => s.Status == ContainerStatus.Running);
        StoppedCount = TotalCount - RunningCount;
    }

    private void OnContainersUpdated(List<ContainerInfo> containers)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var importedDirs = Projects
                .SelectMany(p => p.ComposeFiles)
                .Select(f => f.DirectoryPath)
                .Where(d => !string.IsNullOrEmpty(d));
            var matcher = new ContainerMatcher(containers, importedDirs);

            // 同名 service 出現在多個資料夾（同專案不同分支）時，禁用不分資料夾的寬鬆比對
            var ambiguousNames = Projects
                .SelectMany(p => p.ComposeFiles)
                .SelectMany(f => f.Services)
                .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(s => s.WorkingDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var project in Projects)
            {
                foreach (var compose in project.ComposeFiles)
                {
                    foreach (var service in compose.Services)
                    {
                        var match = matcher.Resolve(
                            service.Name, service.ContainerName, service.WorkingDirectory,
                            compose.ProjectName, ambiguousNames);

                        if (match != null)
                        {
                            service.Status = ContainerMonitorService.ParseStatus(match.State);
                            service.ContainerId = match.ID;
                            if (string.IsNullOrEmpty(service.Ports) && !string.IsNullOrEmpty(match.Ports))
                                service.Ports = match.Ports;
                        }
                        else
                        {
                            service.Status = ContainerStatus.Stopped;
                        }
                    }
                }
            }

            UpdateCounts();
        });
    }

    private void OnContainerCrashed(string containerName, ContainerStatus status)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠️ 容器崩潰: {containerName} → {status}");
            try
            {
                _notifyIcon?.ShowBalloonTip(
                    3000,
                    "容器崩潰警告",
                    $"{containerName} 已停止運行 ({status})",
                    Forms.ToolTipIcon.Warning);
            }
            catch (ObjectDisposedException) { }
        });

        _ = FetchCrashLogsAsync(containerName);
    }

    private async Task FetchCrashLogsAsync(string containerName)
    {
        try
        {
            var logs = await _dockerCli.GetContainerLogsAsync(containerName, tail: 30);
            if (string.IsNullOrWhiteSpace(logs)) return;

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ── {containerName} 最後 30 行日誌 ──");
                foreach (var line in logs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    AppendLog($"  {line.TrimEnd()}");
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ── 日誌結束 ──");
            });
        }
        catch (Exception ex)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠️ 無法取得 {containerName} 日誌: {ex.Message}"));
        }
    }

    internal void AppendLog(string message)
    {
        _pendingLogQueue.Enqueue(message);
        if (Interlocked.Exchange(ref _isLogFlushScheduled, 1) == 1)
            return;

        Application.Current?.Dispatcher.InvokeAsync(FlushPendingLogs);
    }

    private void FlushPendingLogs()
    {
        try
        {
            while (_pendingLogQueue.TryDequeue(out var line))
                AppendLogLine(line);
        }
        finally
        {
            Interlocked.Exchange(ref _isLogFlushScheduled, 0);
            if (!_pendingLogQueue.IsEmpty && Interlocked.Exchange(ref _isLogFlushScheduled, 1) == 0)
                Application.Current?.Dispatcher.InvokeAsync(FlushPendingLogs);
        }
    }

    private void AppendLogLine(string message)
    {
        LogLines.Add(message);
        if (LogLines.Count <= 5000) return;
        // Skip(500) 後 Clear + re-add：O(n) 位移 vs 原本 500 次 RemoveAt(0) 各自 O(n) 位移
        var kept = LogLines.Skip(500).ToArray();
        LogLines.Clear();
        foreach (var line in kept)
            LogLines.Add(line);
    }

    public List<string> ParsePortLinks(string? ports)
    {
        if (string.IsNullOrWhiteSpace(ports)) return [];

        var links = new List<string>();
        var matches = Regex.Matches(ports, @"(?:[\[\]0-9a-fA-F.:]+:)?(\d+)->");
        foreach (Match m in matches)
            links.Add($"http://localhost:{m.Groups[1].Value}");

        return links.Distinct().ToList();
    }

    // 忙碌 = 有人持有 CTS 或已亮起操作旗標：部分操作（Fast Dev 偵測階段、git 切分支）只設旗標未建 CTS，兩者都要看
    private bool IsBusy => _operationCts != null || IsOperating;

    // _operationCts 同時只能被一個操作持有，搶佔前先判斷是否已被佔用，避免後續操作把前一個還在用的 CTS Dispose 掉
    private bool TryBeginOperation()
    {
        if (IsBusy)
        {
            StatusMessage = "⚠ 已有操作進行中，請稍候";
            return false;
        }
        _operationCts = new CancellationTokenSource();
        return true;
    }

    [RelayCommand]
    private void CancelOperation()
    {
        if (!IsOperating) return;
        IsCancelling = true;
        StatusMessage = "⏹ 正在取消操作...";
        _operationCts?.Cancel();
    }

    public void Dispose()
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        StopLogStream();
        StopAllFrontendProcesses();
        _monitor.ContainersUpdated -= OnContainersUpdated;
        _monitor.ContainerCrashed -= OnContainerCrashed;
        _monitor.Dispose();
        _fastDevReload.Dispose();
        GC.SuppressFinalize(this);
    }
}
