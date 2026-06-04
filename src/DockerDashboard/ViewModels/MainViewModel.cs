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
    private readonly WatchRebuildService _watchService;
    private readonly UpdateService _updateService;
    private Forms.NotifyIcon? _notifyIcon;
    private readonly ConcurrentQueue<string> _pendingLogQueue = new();
    private int _isLogFlushScheduled;
    private int _batchStartupParallelism = 3;

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
    private DockerService? _selectedService;

    [ObservableProperty]
    private ComposeFile? _selectedComposeFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAllUp))]
    [NotifyPropertyChangedFor(nameof(CanAllDown))]
    [NotifyPropertyChangedFor(nameof(CanRebuild))]
    private bool _isOperating;

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
    public bool CanRebuild => !IsOperating && TotalCount > 0;
    public bool CanAllDown => !IsOperating && RunningCount > 0;

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
        WatchRebuildService watchService,
        UpdateService updateService)
    {
        _dockerCli = dockerCli;
        _gitService = gitService;
        _scanner = scanner;
        _settingsService = settingsService;
        _monitor = monitor;
        _watchService = watchService;
        _updateService = updateService;

        _monitor.ContainersUpdated += OnContainersUpdated;
        _monitor.ContainerCrashed += OnContainerCrashed;
        _watchService.SetRebuildCallback(OnAutoRebuildTriggeredAsync);

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

            if (!IsDockerAvailable)
            {
                StatusMessage = "⚠ Docker 未啟動或未安裝（請檢查設定）";
                return;
            }
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
                StatusMessage = $"⚠ {project.Name} 中未偵測到服務（docker compose config 可能失敗）";
        }

        _monitor.Start(TimeSpan.FromSeconds(settings.PollIntervalSeconds));
        await _monitor.ForceRefreshAsync();

        ApplyWatchSettings(settings);
        RestoreWatchStateFromSettings(settings);

        StatusMessage = "就緒";

        // 背景靜默檢查更新，不阻塞啟動
        if (settings.AutoCheckUpdate)
            _ = CheckUpdateAsync();
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

    internal void ApplyWatchSettings(AppSettings settings)
    {
        _watchService.IsEnabled = settings.AutoWatchEnabled;
        _watchService.DebounceDelay = TimeSpan.FromSeconds(settings.WatchDebounceSeconds);
        _batchStartupParallelism = Math.Clamp(settings.StartupParallelism, 1, 8);
    }

    internal void RestoreWatchStateFromSettings(AppSettings settings)
    {
        foreach (var project in Projects)
        {
            foreach (var compose in project.ComposeFiles)
            {
                foreach (var service in compose.Services)
                {
                    service.IsWatching = settings.WatchEnabledServiceKeys.Contains(service.WatchKey);
                    if (service.IsWatching)
                        _watchService.AddWatch(service.WorkingDirectory, service.Name);
                }
            }
        }
    }

    private async Task OnAutoRebuildTriggeredAsync(string workingDirectory, string serviceName)
    {
        AppendLog($"[{DateTime.Now:HH:mm:ss}] 👁 Auto Watch: {serviceName} 偵測到變動，開始自動重建...");
        try
        {
            var (exitCode, _) = await _dockerCli.ComposeRebuildRestartWithLogAsync(
                workingDirectory, AppendLog, serviceName);
            if (exitCode == 0)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ Auto Watch: {serviceName} 自動重建完成");
            else
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ Auto Watch: {serviceName} 自動重建失敗 (exit {exitCode})");
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ Auto Watch: {serviceName} 例外: {ex.Message}");
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
        }
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

    private async Task<DockerProject> BuildProjectAsync(string folderPath)
    {
        var project = new DockerProject
        {
            Name = System.IO.Path.GetFileName(folderPath),
            FolderPath = folderPath
        };

        var composeFiles = await _scanner.ScanFolderAsync(folderPath);
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

    internal async Task AddProjectFromFolderAsync(string folderPath)
    {
        var project = await BuildProjectAsync(folderPath);
        Projects.Add(project);

        if (project.ComposeFiles.Count == 0)
            StatusMessage = $"⚠ {project.Name} 中未偵測到服務（docker compose config 可能失敗）";
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
        settings.ImportedFolders = [.. Projects.Select(p => p.FolderPath)];
        settings.RecentlyRemovedFolders = [.. RecentlyRemovedFolders];
        settings.WatchEnabledServiceKeys = [.. Projects
            .SelectMany(p => p.ComposeFiles)
            .SelectMany(c => c.Services)
            .Where(s => s.IsWatching)
            .Select(s => s.WatchKey)];
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
            // 預先建立查找表，將 O(n²) 降為 O(n)
            var byName = new Dictionary<string, ContainerInfo>(StringComparer.OrdinalIgnoreCase);
            var byComposeService = new Dictionary<string, ContainerInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in containers)
            {
                foreach (var name in c.Names.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
                    byName.TryAdd(name.TrimStart('/'), c);

                const string svcPrefix = "com.docker.compose.service=";
                foreach (var label in c.Labels.Split(','))
                {
                    if (label.StartsWith(svcPrefix, StringComparison.OrdinalIgnoreCase))
                        byComposeService.TryAdd(label[svcPrefix.Length..].Trim(), c);
                }
            }

            foreach (var project in Projects)
            {
                foreach (var compose in project.ComposeFiles)
                {
                    foreach (var service in compose.Services)
                    {
                        ContainerInfo? match = null;

                        if (!string.IsNullOrEmpty(service.ContainerName))
                            byName.TryGetValue(service.ContainerName, out match);

                        match ??= byComposeService.GetValueOrDefault(service.Name);
                        match ??= byName.GetValueOrDefault(service.Name);

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

    public void Dispose()
    {
        StopLogStream();
        _monitor.ContainersUpdated -= OnContainersUpdated;
        _monitor.ContainerCrashed -= OnContainerCrashed;
        _monitor.Dispose();
        _watchService.Dispose();
        GC.SuppressFinalize(this);
    }
}
