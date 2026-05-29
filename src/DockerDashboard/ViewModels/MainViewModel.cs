using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerDashboard.Models;
using DockerDashboard.Services;
using Microsoft.Win32;
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
    private ProcessStream? _logProcess;
    private CancellationTokenSource? _logCts;
    private Forms.NotifyIcon? _notifyIcon;

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
    private bool _isOperating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAllUp))]
    [NotifyPropertyChangedFor(nameof(CanAllDown))]
    private int _runningCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAllUp))]
    [NotifyPropertyChangedFor(nameof(CanAllDown))]
    private int _totalCount;

    [ObservableProperty]
    private int _stoppedCount;

    public bool CanAllUp => !IsOperating && TotalCount > 0 && RunningCount < TotalCount;
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
        ContainerMonitorService monitor)
    {
        _dockerCli = dockerCli;
        _gitService = gitService;
        _scanner = scanner;
        _settingsService = settingsService;
        _monitor = monitor;

        _monitor.ContainersUpdated += OnContainersUpdated;
        _monitor.ContainerCrashed += OnContainerCrashed;

        LogView = CollectionViewSource.GetDefaultView(LogLines);
        LogView.Filter = LogFilterPredicate;
    }

    partial void OnLogFilterChanged(string value)
    {
        LogView?.Refresh();
    }

    private bool LogFilterPredicate(object item)
    {
        if (string.IsNullOrWhiteSpace(LogFilter)) return true;
        return item is string line && line.Contains(LogFilter, StringComparison.OrdinalIgnoreCase);
    }

    public void SetNotifyIcon(Forms.NotifyIcon? icon)
    {
        _notifyIcon = icon;
    }

    public async Task InitializeAsync()
    {
        var settings = await _settingsService.LoadAsync();
        ApplyDockerModeSettings(settings);

        IsDockerAvailable = await _dockerCli.IsDockerAvailableAsync();
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

                IsDockerAvailable = await _dockerCli.IsDockerAvailableAsync();
            }

            if (!IsDockerAvailable)
            {
                StatusMessage = "⚠ Docker 未啟動或未安裝（請檢查設定）";
                return;
            }
        }

        foreach (var folder in settings.RecentlyRemovedFolders)
            RecentlyRemovedFolders.Add(folder);

        foreach (var folder in settings.ImportedFolders)
        {
            if (Directory.Exists(folder))
                await AddProjectFromFolderAsync(folder);
        }

        _monitor.Start(TimeSpan.FromSeconds(settings.PollIntervalSeconds));
        await _monitor.ForceRefreshAsync();
        StatusMessage = "就緒";
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "選擇專案資料夾",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true) return;

        foreach (var folder in dialog.FolderNames)
        {
            if (Projects.Any(p => p.FolderPath.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                continue;

            await AddProjectFromFolderAsync(folder);
            RemoveRecentFolder(folder);
        }

        await SaveSettingsAsync();
        StatusMessage = $"已匯入 {Projects.Count} 個專案";
    }

    [RelayCommand]
    private async Task ImportRecentFolderAsync(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        if (Projects.Any(p => p.FolderPath.Equals(folder, StringComparison.OrdinalIgnoreCase))) return;

        await AddProjectFromFolderAsync(folder);
        RemoveRecentFolder(folder);
        await SaveSettingsAsync();
        StatusMessage = $"已重新匯入 {Path.GetFileName(folder)}";
    }

    [RelayCommand]
    private async Task AllUpAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        IsOperating = true;
        StatusMessage = "正在並行啟動所有服務（快速模式）...";
        LogLines.Clear();
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ▶ 全部啟動（快速模式，不重建 image）");

        var composeFiles = Projects.SelectMany(p => p.ComposeFiles).ToList();
        var errors = new ConcurrentBag<string>();

        var tasks = composeFiles.Select(async compose =>
        {
            try
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] 啟動 {compose.FileName} ...");
                var (exitCode, _) = await _dockerCli.ComposeUpFastWithLogAsync(
                    compose.DirectoryPath, AppendLog);
                if (exitCode != 0)
                    errors.Add(compose.FileName);
                else
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 啟動完成");
            }
            catch (Exception ex)
            {
                errors.Add($"{compose.FileName}: {ex.Message}");
                AppendLog($"[例外] {ex.Message}");
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }

        if (!errors.IsEmpty)
        {
            StatusMessage = $"⚠ {errors.Count} 個服務啟動失敗";
            foreach (var err in errors)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {err}");
        }
        else
        {
            StatusMessage = "✅ 所有服務已啟動";
        }
    }

    [RelayCommand]
    private async Task AllUpBuildAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        IsOperating = true;
        StatusMessage = "正在並行啟動所有服務（含重建 image）...";
        LogLines.Clear();
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ▶ 全部啟動（含重建 image，耗時較長）");

        var composeFiles = Projects.SelectMany(p => p.ComposeFiles).ToList();
        var errors = new ConcurrentBag<string>();

        var tasks = composeFiles.Select(async compose =>
        {
            try
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] 重建+啟動 {compose.FileName} ...");
                var (exitCode, _) = await _dockerCli.ComposeUpWithLogAsync(
                    compose.DirectoryPath, AppendLog);
                if (exitCode != 0)
                    errors.Add(compose.FileName);
                else
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 重建+啟動完成");
            }
            catch (Exception ex)
            {
                errors.Add($"{compose.FileName}: {ex.Message}");
                AppendLog($"[例外] {ex.Message}");
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }

        if (!errors.IsEmpty)
        {
            StatusMessage = $"⚠ {errors.Count} 個服務重建失敗";
            foreach (var err in errors)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {err}");
        }
        else
        {
            StatusMessage = "✅ 所有服務已重建並啟動";
        }
    }

    [RelayCommand]
    private async Task AllDownAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        IsOperating = true;
        StatusMessage = "正在並行停止所有服務...";
        LogLines.Clear();
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ■ 全部停止（並行）");

        var composeFiles = Projects.SelectMany(p => p.ComposeFiles).ToList();
        var errors = new ConcurrentBag<string>();

        var tasks = composeFiles.Select(async compose =>
        {
            try
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] 停止 {compose.FileName} ...");
                var (exitCode, _) = await _dockerCli.ComposeDownWithLogAsync(
                    compose.DirectoryPath, AppendLog);
                if (exitCode != 0)
                    errors.Add(compose.FileName);
                else
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 停止完成");
            }
            catch (Exception ex)
            {
                errors.Add($"{compose.FileName}: {ex.Message}");
                AppendLog($"[例外] {ex.Message}");
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }

        if (!errors.IsEmpty)
        {
            StatusMessage = $"⚠ {errors.Count} 個服務停止失敗";
            foreach (var err in errors)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {err}");
        }
        else
        {
            StatusMessage = "✅ 所有服務已停止";
        }
    }

    [RelayCommand]
    private async Task StartServiceAsync(DockerService? service)
    {
        if (service == null) return;
        IsOperating = true;
        StatusMessage = $"正在啟動 {service.Name}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ▶ 啟動 {service.Name}");

        try
        {
            var (exitCode, _) = await _dockerCli.ComposeUpFastWithLogAsync(
                service.WorkingDirectory, AppendLog, service.Name);
            if (exitCode != 0)
            {
                StatusMessage = $"⚠ {service.Name} 啟動失敗";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {service.Name} 啟動失敗 (exit code: {exitCode})");
            }
            else
            {
                StatusMessage = $"✅ {service.Name} 已啟動";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {service.Name} 啟動完成");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ {service.Name} 啟動失敗: {ex.Message}";
            AppendLog($"[例外] {ex.Message}");
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task StopServiceAsync(DockerService? service)
    {
        if (service == null) return;
        IsOperating = true;
        StatusMessage = $"正在停止 {service.Name}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ■ 停止 {service.Name}");

        try
        {
            var (exitCode, _) = await _dockerCli.ComposeStopWithLogAsync(
                service.WorkingDirectory, AppendLog, service.Name);
            if (exitCode != 0)
            {
                StatusMessage = $"⚠ {service.Name} 停止失敗";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {service.Name} 停止失敗 (exit code: {exitCode})");
            }
            else
            {
                StatusMessage = $"✅ {service.Name} 已停止";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {service.Name} 停止完成");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ {service.Name} 停止失敗: {ex.Message}";
            AppendLog($"[例外] {ex.Message}");
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task RestartServiceAsync(DockerService? service)
    {
        if (service == null) return;
        IsOperating = true;
        StatusMessage = $"正在重啟 {service.Name}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] 🔄 重啟 {service.Name}");

        try
        {
            var (exitCode, _) = await _dockerCli.ComposeRestartWithLogAsync(
                service.WorkingDirectory, AppendLog, service.Name);
            if (exitCode != 0)
            {
                StatusMessage = $"⚠ {service.Name} 重啟失敗";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {service.Name} 重啟失敗 (exit code: {exitCode})");
            }
            else
            {
                StatusMessage = $"✅ {service.Name} 已重啟";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {service.Name} 重啟完成");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ {service.Name} 重啟失敗: {ex.Message}";
            AppendLog($"[例外] {ex.Message}");
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task ComposeUpAsync(ComposeFile? compose)
    {
        if (compose == null) return;
        IsOperating = true;
        StatusMessage = $"正在啟動 {compose.FileName}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ▶ 啟動 {compose.FileName}");

        try
        {
            var (exitCode, _) = await _dockerCli.ComposeUpFastWithLogAsync(compose.DirectoryPath, AppendLog);
            if (exitCode == 0)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 啟動完成");
            else
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 啟動失敗");
            StatusMessage = exitCode == 0 ? $"✅ {compose.FileName} 已啟動" : $"⚠ {compose.FileName} 啟動失敗";
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 例外: {ex.Message}");
            StatusMessage = $"⚠ {compose.FileName} 啟動失敗";
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task ComposeDownAsync(ComposeFile? compose)
    {
        if (compose == null) return;
        IsOperating = true;
        StatusMessage = $"正在停止 {compose.FileName}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ■ 停止 {compose.FileName}");

        try
        {
            var (exitCode, _) = await _dockerCli.ComposeDownWithLogAsync(compose.DirectoryPath, AppendLog);
            if (exitCode == 0)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 停止完成");
            else
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 停止失敗");
            StatusMessage = exitCode == 0 ? $"✅ {compose.FileName} 已停止" : $"⚠ {compose.FileName} 停止失敗";
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 例外: {ex.Message}");
            StatusMessage = $"⚠ {compose.FileName} 停止失敗";
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task ComposePullAsync(ComposeFile? compose)
    {
        if (compose == null) return;
        IsOperating = true;
        StatusMessage = $"正在拉取 {compose.FileName} 映像...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ⬇ 拉取映像 {compose.FileName}");

        try
        {
            var (exitCode, _) = await _dockerCli.ComposePullWithLogAsync(compose.DirectoryPath, AppendLog);
            if (exitCode == 0)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 映像拉取完成");
            else
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 映像拉取失敗");
            StatusMessage = exitCode == 0 ? $"✅ {compose.FileName} 映像已更新" : $"⚠ {compose.FileName} 映像拉取失敗";
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 例外: {ex.Message}");
            StatusMessage = $"⚠ {compose.FileName} 映像拉取失敗";
        }
        finally
        {
            IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task PullAllImagesAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        IsOperating = true;
        StatusMessage = "正在拉取所有映像...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ⬇ 拉取所有映像（並行）");

        var composeFiles = Projects.SelectMany(p => p.ComposeFiles).ToList();
        var errors = new ConcurrentBag<string>();

        var tasks = composeFiles.Select(async compose =>
        {
            try
            {
                var (exitCode, _) = await _dockerCli.ComposePullWithLogAsync(compose.DirectoryPath, AppendLog);
                if (exitCode != 0) errors.Add(compose.FileName);
                else AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 映像拉取完成");
            }
            catch (Exception ex)
            {
                errors.Add($"{compose.FileName}: {ex.Message}");
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            IsOperating = false;
        }
        StatusMessage = errors.IsEmpty ? "✅ 所有映像已更新" : $"⚠ {errors.Count} 個映像拉取失敗";
    }

    [RelayCommand]
    private void ViewLogs(DockerService? service)
    {
        if (service == null) return;
        StopLogStream();
        LogLines.Clear();

        var containerName = !string.IsNullOrEmpty(service.ContainerId)
            ? service.ContainerId
            : !string.IsNullOrEmpty(service.ContainerName)
                ? service.ContainerName
                : null;

        _logCts = new CancellationTokenSource();

        if (!string.IsNullOrEmpty(containerName))
        {
            _logProcess = _dockerCli.StartLogStream(containerName);
        }
        else
        {
            _logProcess = _dockerCli.StartComposeLogStream(service.WorkingDirectory, service.Name);
        }

        Task.Run(() => ReadLogStreamAsync(_logProcess, _logCts.Token));
        StatusMessage = $"正在串流 {service.Name} 的日誌...";
    }

    [RelayCommand]
    private void StopLogs()
    {
        StopLogStream();
        StatusMessage = "日誌串流已停止";
    }

    [RelayCommand]
    private void ClearLogs()
    {
        LogLines.Clear();
    }

    [RelayCommand]
    private async Task ExportLogsAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "匯出日誌",
            Filter = "文字檔 (*.txt)|*.txt|日誌檔 (*.log)|*.log",
            FileName = $"docker-logs-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };

        if (dialog.ShowDialog() != true) return;

        var snapshot = LogLines.ToList();
        await File.WriteAllLinesAsync(dialog.FileName, snapshot);
        StatusMessage = $"日誌已匯出到 {dialog.FileName}";
    }

    [RelayCommand]
    private async Task RemoveProjectAsync(DockerProject? project)
    {
        if (project == null) return;

        if (!RecentlyRemovedFolders.Contains(project.FolderPath))
        {
            RecentlyRemovedFolders.Add(project.FolderPath);
            if (RecentlyRemovedFolders.Count > 10)
                RecentlyRemovedFolders.RemoveAt(0);
        }

        Projects.Remove(project);
        await SaveSettingsAsync();
        UpdateCounts();
        StatusMessage = $"已移除 {project.Name}";
    }

    [RelayCommand]
    private async Task RescanProjectsAsync()
    {
        var folders = Projects.Select(p => p.FolderPath).ToList();
        Projects.Clear();

        var results = await Task.WhenAll(
            folders.Select(async folder =>
            {
                var project = new DockerProject
                {
                    Name = Path.GetFileName(folder),
                    FolderPath = folder
                };
                if (Directory.Exists(folder))
                {
                    var composeFiles = await Task.Run(() => _scanner.ScanFolder(folder));
                    foreach (var cf in composeFiles)
                        project.ComposeFiles.Add(cf);
                }
                return project;
            }));

        foreach (var project in results)
        {
            Projects.Add(project);
            if (Directory.Exists(project.FolderPath) && project.ComposeFiles.Count == 0)
                StatusMessage = $"⚠ {project.Name} 中未偵測到服務（docker compose config 可能失敗）";
        }

        await _monitor.ForceRefreshAsync();
        StatusMessage = "重新掃描完成";
    }

    [RelayCommand]
    private void OpenInBrowser(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var settings = await _settingsService.LoadAsync();
        var settingsWindow = new Views.SettingsWindow(settings);
        settingsWindow.Owner = Application.Current.MainWindow;

        if (settingsWindow.ShowDialog() == true)
        {
            await _settingsService.SaveAsync(settings);
            ApplyDockerModeSettings(settings);
            await Task.Run(() => _monitor.Stop());
            _monitor.Start(TimeSpan.FromSeconds(settings.PollIntervalSeconds));
            StatusMessage = "設定已儲存並套用";
        }
    }

    private void ApplyDockerModeSettings(AppSettings settings)
    {
        _dockerCli.UseComposeV2 = settings.UseComposeV2;
        _dockerCli.DockerMode = settings.DockerMode;
        _dockerCli.WslDistroName = settings.WslDistroName;
        DockerModeLabel = settings.DockerMode == DockerMode.Wsl2
            ? $"WSL2 ({settings.WslDistroName})"
            : "Docker Desktop";
    }

    [RelayCommand]
    private async Task SwitchBranchAsync(DockerProject? project)
    {
        if (project == null || !project.IsGitRepo) return;

        if (project.IsDirty)
        {
            var confirm = System.Windows.MessageBox.Show(
                $"專案 {project.Name} 有未提交的變更，切換分支可能導致衝突。\n\n確定要繼續嗎？",
                "未提交變更警告",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (confirm != System.Windows.MessageBoxResult.Yes) return;
        }

        var localBranches = await _gitService.GetLocalBranchesAsync(project.FolderPath);
        var remoteBranches = await _gitService.GetRemoteBranchesAsync(project.FolderPath);

        var selector = new Views.BranchSelectorWindow(
            project.Name, project.CurrentBranch, localBranches, remoteBranches);
        selector.Owner = Application.Current.MainWindow;

        if (selector.ShowDialog() != true || string.IsNullOrEmpty(selector.SelectedBranch))
            return;

        IsOperating = true;
        var targetBranch = selector.SelectedBranch;
        StatusMessage = $"正在切換 {project.Name} 到分支 {targetBranch}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] 🔀 切換 {project.Name} → {targetBranch}");

        try
        {
            var (success, output) = await _gitService.CheckoutAsync(project.FolderPath, targetBranch);

            if (success)
            {
                project.CurrentBranch = await _gitService.GetCurrentBranchAsync(project.FolderPath);
                project.IsDirty = await _gitService.IsDirtyAsync(project.FolderPath);
                StatusMessage = $"✅ {project.Name} 已切換到 {project.CurrentBranch}";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ 分支切換成功: {project.CurrentBranch}");
            }
            else
            {
                StatusMessage = $"⚠ {project.Name} 分支切換失敗";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ 分支切換失敗: {output}");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ {project.Name} 分支切換失敗";
            AppendLog($"[例外] {ex.Message}");
        }
        finally
        {
            IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task RefreshGitStatusAsync(DockerProject? project)
    {
        if (project == null || !project.IsGitRepo) return;

        project.CurrentBranch = await _gitService.GetCurrentBranchAsync(project.FolderPath);
        project.IsDirty = await _gitService.IsDirtyAsync(project.FolderPath);
    }

    partial void OnSelectedServiceChanged(DockerService? value)
    {
        if (value != null)
            ViewLogs(value);
    }

    public List<string> ParsePortLinks(string? ports)
    {
        if (string.IsNullOrWhiteSpace(ports)) return [];

        var links = new List<string>();
        // [\[\]0-9a-fA-F.:]+ covers IPv4, IPv6 (with or without brackets), and multiple colons
        var matches = Regex.Matches(ports, @"(?:[\[\]0-9a-fA-F.:]+:)?(\d+)->");
        foreach (Match m in matches)
            links.Add($"http://localhost:{m.Groups[1].Value}");

        return links.Distinct().ToList();
    }

    private async Task AddProjectFromFolderAsync(string folderPath)
    {
        var project = new DockerProject
        {
            Name = Path.GetFileName(folderPath),
            FolderPath = folderPath
        };

        var composeFiles = await Task.Run(() => _scanner.ScanFolder(folderPath));
        foreach (var cf in composeFiles)
            project.ComposeFiles.Add(cf);

        if (_gitService.IsGitRepository(folderPath))
        {
            project.IsGitRepo = true;
            project.CurrentBranch = await _gitService.GetCurrentBranchAsync(folderPath);
            project.IsDirty = await _gitService.IsDirtyAsync(folderPath);
        }

        Projects.Add(project);

        if (project.ComposeFiles.Count == 0)
            StatusMessage = $"⚠ {project.Name} 中未偵測到服務（docker compose config 可能失敗）";
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

    private void OnContainersUpdated(List<ContainerInfo> containers)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            foreach (var project in Projects)
            {
                foreach (var compose in project.ComposeFiles)
                {
                    foreach (var service in compose.Services)
                    {
                        var match = containers.FirstOrDefault(c =>
                            (!string.IsNullOrEmpty(service.ContainerName) &&
                             c.Names.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                              .Any(n => n.TrimStart('/').Equals(service.ContainerName, StringComparison.OrdinalIgnoreCase))) ||
                            c.Labels.Contains($"com.docker.compose.service={service.Name},", StringComparison.OrdinalIgnoreCase) ||
                            c.Labels.EndsWith($"com.docker.compose.service={service.Name}", StringComparison.OrdinalIgnoreCase) ||
                            c.Names.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                             .Any(n => n.TrimStart('/').Equals(service.Name, StringComparison.OrdinalIgnoreCase)));

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

    private void AppendLog(string message)
    {
        Application.Current?.Dispatcher.InvokeAsync(() => AppendLogLine(message));
    }

    private void AppendLogLine(string message)
    {
        LogLines.Add(message);
        if (LogLines.Count > 5000)
        {
            var toKeep = LogLines.Skip(500).ToList();
            LogLines.Clear();
            foreach (var line in toKeep)
                LogLines.Add(line);
        }
    }

    private async Task ReadLogStreamAsync(ProcessStream stream, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(
                ReadStreamAsync(stream.StandardOutput, ct),
                ReadStreamAsync(stream.StandardError, ct));
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReadStreamAsync(StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            Application.Current?.Dispatcher.InvokeAsync(() => AppendLogLine(line));
        }
    }

    private void StopLogStream()
    {
        _logCts?.Cancel();
        _logProcess?.Dispose();
        _logProcess = null;
        _logCts?.Dispose();
        _logCts = null;
    }

    private void RemoveRecentFolder(string folder)
    {
        var existing = RecentlyRemovedFolders.FirstOrDefault(
            f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            RecentlyRemovedFolders.Remove(existing);
    }

    private async Task SaveSettingsAsync()
    {
        var settings = await _settingsService.LoadAsync();
        settings.ImportedFolders = [.. Projects.Select(p => p.FolderPath)];
        settings.RecentlyRemovedFolders = [.. RecentlyRemovedFolders];
        await _settingsService.SaveAsync(settings);
    }

    public void Dispose()
    {
        StopLogStream();
        _monitor.ContainersUpdated -= OnContainersUpdated;
        _monitor.ContainerCrashed -= OnContainerCrashed;
        _monitor.Dispose();
        GC.SuppressFinalize(this);
    }
}
