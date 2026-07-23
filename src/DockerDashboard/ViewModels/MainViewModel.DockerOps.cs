using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DockerDashboard.Models;
using DockerDashboard.Services;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace DockerDashboard.ViewModels;

public partial class MainViewModel
{
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

    [RelayCommand]
    private async Task AllUpAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        if (!await ConfirmBatchOverridesFastDevAsync()) return;

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
    private async Task AllDownAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        if (!await ConfirmBatchOverridesFastDevAsync()) return;

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

    [RelayCommand]
    private async Task StartServiceAsync(DockerService? service)
    {
        if (service == null) return;

        var cts = new CancellationTokenSource();
        _operationCts?.Dispose();
        _operationCts = cts;
        var ct = cts.Token;

        IsOperating = true;
        IsCancelling = false;
        StatusMessage = $"正在啟動 {service.Name}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ▶ 啟動 {service.Name}");

        bool wasCancelled = false;
        try
        {
            var (exitCode, _) = await _dockerCli.ComposeUpFastWithLogAsync(
                service.WorkingDirectory, AppendLog, service.Name, ct);
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
        catch (OperationCanceledException)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ {service.Name} 啟動已取消");
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ {service.Name} 啟動失敗: {ex.Message}";
            AppendLog($"[例外] {ex.Message}");
        }
        finally
        {
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
    }

    [RelayCommand]
    private async Task StopServiceAsync(DockerService? service)
    {
        if (service == null) return;

        var cts = new CancellationTokenSource();
        _operationCts?.Dispose();
        _operationCts = cts;
        var ct = cts.Token;

        IsOperating = true;
        IsCancelling = false;
        StatusMessage = $"正在停止 {service.Name}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ■ 停止 {service.Name}");

        bool wasCancelled = false;
        try
        {
            var (exitCode, _) = await _dockerCli.ComposeStopWithLogAsync(
                service.WorkingDirectory, AppendLog, service.Name, ct);
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
        catch (OperationCanceledException)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ {service.Name} 停止已取消");
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ {service.Name} 停止失敗: {ex.Message}";
            AppendLog($"[例外] {ex.Message}");
        }
        finally
        {
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
    }

    [RelayCommand]
    private async Task RestartServiceAsync(DockerService? service)
    {
        if (service == null) return;

        var cts = new CancellationTokenSource();
        _operationCts?.Dispose();
        _operationCts = cts;
        var ct = cts.Token;

        IsOperating = true;
        IsCancelling = false;
        StatusMessage = $"正在重啟 {service.Name}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] 🔄 重啟 {service.Name}（不重建 image）");

        bool wasCancelled = false;
        try
        {
            var (exitCode, _) = await _dockerCli.ComposeRestartWithLogAsync(
                service.WorkingDirectory, AppendLog, service.Name, ct);
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
        catch (OperationCanceledException)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ {service.Name} 重啟已取消");
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ {service.Name} 重啟失敗: {ex.Message}";
            AppendLog($"[例外] {ex.Message}");
        }
        finally
        {
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
    }

    [RelayCommand]
    private async Task RebuildRestartServiceAsync(DockerService? service)
    {
        if (service == null) return;

        var cts = new CancellationTokenSource();
        _operationCts?.Dispose();
        _operationCts = cts;
        var ct = cts.Token;

        IsOperating = true;
        IsCancelling = false;
        StatusMessage = $"正在重建並重啟 {service.Name}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] 🔨 重建重啟 {service.Name}（重新 build image）");

        bool wasCancelled = false;
        try
        {
            var (exitCode, _) = await _dockerCli.ComposeRebuildRestartWithLogAsync(
                service.WorkingDirectory, AppendLog, service.Name, ct);
            if (exitCode != 0)
            {
                StatusMessage = $"⚠ {service.Name} 重建重啟失敗";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {service.Name} 重建重啟失敗 (exit code: {exitCode})");
            }
            else
            {
                StatusMessage = $"✅ {service.Name} 已重建並重啟";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {service.Name} 重建重啟完成");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ {service.Name} 重建已取消");
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ {service.Name} 重建重啟失敗: {ex.Message}";
            AppendLog($"[例外] {ex.Message}");
        }
        finally
        {
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
    }

    [RelayCommand]
    private async Task ComposeUpAsync(ComposeFile? compose)
    {
        if (compose == null) return;

        var cts = new CancellationTokenSource();
        _operationCts?.Dispose();
        _operationCts = cts;
        var ct = cts.Token;

        IsOperating = true;
        IsCancelling = false;
        StatusMessage = $"正在啟動 {compose.FileName}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ▶ 啟動 {compose.FileName}");

        bool wasCancelled = false;
        try
        {
            var (exitCode, _) = await _dockerCli.ComposeUpFastWithLogAsync(compose.DirectoryPath, AppendLog, ct: ct);
            if (exitCode == 0)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 啟動完成");
            else
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 啟動失敗");
            StatusMessage = exitCode == 0 ? $"✅ {compose.FileName} 已啟動" : $"⚠ {compose.FileName} 啟動失敗";
        }
        catch (OperationCanceledException)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ {compose.FileName} 已取消");
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 例外: {ex.Message}");
            StatusMessage = $"⚠ {compose.FileName} 啟動失敗";
        }
        finally
        {
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
    }

    [RelayCommand]
    private async Task ComposeDownAsync(ComposeFile? compose)
    {
        if (compose == null) return;

        var cts = new CancellationTokenSource();
        _operationCts?.Dispose();
        _operationCts = cts;
        var ct = cts.Token;

        IsOperating = true;
        IsCancelling = false;
        StatusMessage = $"正在停止 {compose.FileName}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ■ 停止 {compose.FileName}");

        bool wasCancelled = false;
        try
        {
            var (exitCode, _) = await _dockerCli.ComposeDownWithLogAsync(compose.DirectoryPath, AppendLog, ct);
            if (exitCode == 0)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 停止完成");
            else
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 停止失敗");
            StatusMessage = exitCode == 0 ? $"✅ {compose.FileName} 已停止" : $"⚠ {compose.FileName} 停止失敗";
        }
        catch (OperationCanceledException)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ {compose.FileName} 已取消");
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 例外: {ex.Message}");
            StatusMessage = $"⚠ {compose.FileName} 停止失敗";
        }
        finally
        {
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
    }

    [RelayCommand]
    private async Task RemoveProjectAsync(DockerProject? project)
    {
        if (project == null) return;

        var runningComposes = project.ComposeFiles
            .Where(c => c.Services.Any(s => s.IsRunning))
            .ToList();
        if (runningComposes.Count > 0 && IsDockerAvailable)
        {
            var cts = new CancellationTokenSource();
            _operationCts?.Dispose();
            _operationCts = cts;
            var ct = cts.Token;

            IsOperating = true;
            IsCancelling = false;
            StatusMessage = $"正在停止 {project.Name} 的服務...";
            try
            {
                foreach (var compose in runningComposes)
                {
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] ■ 停止 {compose.FileName}");
                    var (exitCode, _) = await _dockerCli.ComposeStopWithLogAsync(compose.DirectoryPath, AppendLog, null, ct);
                    if (exitCode != 0)
                        AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ {compose.FileName} 停止失敗 (exit code: {exitCode})，仍繼續移除");
                }
            }
            catch (OperationCanceledException)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ 停止已取消，仍繼續移除");
            }
            catch (Exception ex)
            {
                AppendLog($"[例外] 停止服務失敗: {ex.Message}，仍繼續移除");
            }
            finally
            {
                if (ReferenceEquals(_operationCts, cts))
                    _operationCts = null;
                cts.Dispose();
                IsCancelling = false;
                IsOperating = false;
            }
        }

        if (!RecentlyRemovedFolders.Contains(project.FolderPath))
        {
            RecentlyRemovedFolders.Add(project.FolderPath);
            if (RecentlyRemovedFolders.Count > 10)
                RecentlyRemovedFolders.RemoveAt(0);
        }

        Projects.Remove(project);

        var removedServices = project.ComposeFiles.SelectMany(c => c.Services).ToList();
        foreach (var dir in removedServices
                     .Where(s => s.IsFastDev)
                     .Select(s => s.WorkingDirectory)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            _fastDevReload.Unwatch(dir);

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
            folders.Where(Directory.Exists).Select(f => BuildProjectAsync(f, useCache: false)));

        foreach (var project in results)
        {
            Projects.Add(project);
            if (project.ComposeFiles.Count == 0)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ {project.Name} 未偵測到服務（docker compose config 可能失敗）");
        }

        _fastDevReload.ClearAll();
        var settings = await _settingsService.LoadAsync();
        RestoreFastDevStateFromSettings(settings);

        await _monitor.ForceRefreshAsync();
        StatusMessage = "重新掃描完成";
    }

    [RelayCommand]
    private async Task ToggleFastDevServiceAsync(DockerService? service)
    {
        if (service == null) return;
        if (service.IsFastDev) { await DisableFastDevServicesAsync([service]); return; }
        await EnableFastDevServicesAsync(service.WorkingDirectory, [service]);
    }

    [RelayCommand]
    private async Task ToggleProjectFastDevAsync(DockerProject? project)
    {
        if (project == null)
        {
            StatusMessage = "⚠ 請先在左側選一個專案";
            return;
        }
        var services = project.ComposeFiles.SelectMany(c => c.Services).ToList();
        if (services.Any(s => s.IsFastDev))
        {
            await DisableFastDevServicesAsync(services.Where(s => s.IsFastDev).ToList());
            return;
        }
        foreach (var group in services.GroupBy(s => s.WorkingDirectory, StringComparer.OrdinalIgnoreCase))
            await EnableFastDevServicesAsync(group.Key, group.ToList());
    }

    [RelayCommand]
    private async Task ToggleFastDevAutoReloadAsync()
    {
        FastDevAutoReloadEnabled = !FastDevAutoReloadEnabled;
        _fastDevReload.IsEnabled = FastDevAutoReloadEnabled;
        var settings = await _settingsService.LoadAsync();
        settings.FastDevAutoReloadEnabled = FastDevAutoReloadEnabled;
        await _settingsService.SaveAsync(settings);
        AppendLog($"[{DateTime.Now:HH:mm:ss}] 🔥 自動熱重載：{(FastDevAutoReloadEnabled ? "開" : "關")}");
    }

    [RelayCommand]
    private async Task ManualReloadAsync()
    {
        var dirs = Projects.SelectMany(p => p.ComposeFiles).SelectMany(c => c.Services)
            .Where(s => s.IsFastDev)
            .Select(s => s.WorkingDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (dirs.Count == 0)
        {
            StatusMessage = "⚠ 沒有 Fast Dev 服務可重載";
            return;
        }
        foreach (var dir in dirs)
            await OnFastDevSolutionChangedAsync(dir);
    }

    private async Task EnableFastDevServicesAsync(string workingDirectory, IReadOnlyList<DockerService> services)
    {
        if (!await IsDotnetSdkAvailableAsync())
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ 找不到 host 端 dotnet SDK，無法啟用 Fast Dev");
            return;
        }

        var settings = await _settingsService.LoadAsync();
        var enabledAny = false;
        foreach (var service in services)
        {
            var config = ResolveFastDevConfig(service, settings);
            if (config == null)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ {service.Name} 找不到 .csproj，略過");
                continue;
            }
            service.IsFastDev = true;
            PersistFastDev(settings, service.WatchKey, config, enabled: true);
            enabledAny = true;
        }
        if (!enabledAny) return;

        await _settingsService.SaveAsync(settings);
        await ApplyFastDevForDirectoryAsync(workingDirectory, settings);
    }

    private async Task DisableFastDevServicesAsync(IReadOnlyList<DockerService> services)
    {
        var settings = await _settingsService.LoadAsync();
        foreach (var service in services)
        {
            service.IsFastDev = false;
            PersistFastDev(settings, service.WatchKey, null, enabled: false);
        }
        await _settingsService.SaveAsync(settings);

        foreach (var dir in services.Select(s => s.WorkingDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
            await ApplyFastDevForDirectoryAsync(dir, settings);
    }

    // 依目錄目前所有 fastdev 服務產一份合併 override、一次整包 up；無 fastdev 服務則還原成原 image
    private async Task ApplyFastDevForDirectoryAsync(string workingDirectory, AppSettings settings)
    {
        var cts = new CancellationTokenSource();
        _operationCts?.Dispose();
        _operationCts = cts;
        var ct = cts.Token;
        IsOperating = true;
        IsCancelling = false;

        try
        {
            var fastDevServices = Projects.SelectMany(p => p.ComposeFiles).SelectMany(c => c.Services)
                .Where(s => s.IsFastDev &&
                            string.Equals(s.WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (fastDevServices.Count == 0)
            {
                FastDevOverrideStore.Delete(workingDirectory);
                _fastDevReload.Unwatch(workingDirectory);
                StatusMessage = "正在還原（換回原 image）...";
                var (revertExit, _) = await _dockerCli.ComposeUpAllAsync(workingDirectory, AppendLog, ct);
                if (!ct.IsCancellationRequested)
                    StatusMessage = revertExit == 0 ? "✅ 已離開 Fast Dev" : "⚠ 還原可能失敗";
                return;
            }

            var items = new List<(DockerService Service, FastDevConfig Config)>();
            foreach (var service in fastDevServices)
            {
                var config = settings.FastDevConfigs.FirstOrDefault(c => c.ServiceKey == service.WatchKey);
                if (config != null) items.Add((service, config));
            }
            if (items.Count == 0) return;

            StatusMessage = "正在 host build（首次較久，可取消）...";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 🔨 host build {workingDirectory}");
            var detection = FastDevDetector.Detect(workingDirectory, items[0].Service.Name, settings.DefaultRuntimeImage);
            var fallbackProjects = items
                .Select(i => Path.Combine(workingDirectory, i.Config.CsprojRelativePath.Replace('/', Path.DirectorySeparatorChar)))
                .ToList();
            var (buildExit, _) = await _hostBuild.BuildAsync(detection.SolutionPath, fallbackProjects, AppendLog, ct);
            if (ct.IsCancellationRequested) return;
            if (buildExit != 0)
            {
                StatusMessage = "⚠ host build 失敗";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ host build 失敗 (exit {buildExit})");
                return;
            }

            var blocks = items.Select(i =>
            {
                var (appDirHost, nugetHost) = ResolveFastDevHostPaths(i.Config);
                return FastDevComposeGenerator.ServiceOverrideBlock(i.Service.Name, i.Config, appDirHost, nugetHost);
            });
            var yaml = FastDevComposeGenerator.CombineOverride(blocks);
            var overridePath = FastDevOverrideStore.Write(workingDirectory, yaml);

            StatusMessage = "正在整包啟動 Fast Dev...";
            var (upExit, _) = await _dockerCli.ComposeUpAllAsync(workingDirectory, AppendLog, ct, overridePath);
            if (ct.IsCancellationRequested) return;
            if (upExit != 0)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ Fast Dev 啟動失敗 (exit {upExit})");
            else
            {
                _fastDevReload.Watch(workingDirectory);
                StatusMessage = "✅ Fast Dev 就緒（改 .cs 約 5 秒生效）";
            }
        }
        finally
        {
            var wasCancelled = cts.IsCancellationRequested;
            if (ReferenceEquals(_operationCts, cts))
                _operationCts = null;
            cts.Dispose();
            IsCancelling = false;
            IsOperating = false;
            if (wasCancelled)
            {
                StatusMessage = "⏹ Fast Dev 已取消";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ Fast Dev 操作已取消");
            }
            await _monitor.ForceRefreshAsync();
        }
    }

    private async Task OnFastDevSolutionChangedAsync(string workingDirectory)
    {
        var settings = await _settingsService.LoadAsync();
        var fastDevServices = Projects.SelectMany(p => p.ComposeFiles).SelectMany(c => c.Services)
            .Where(s => s.IsFastDev &&
                        string.Equals(s.WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (fastDevServices.Count == 0) return;

        var before = new Dictionary<string, (string DllPath, long BeforeTicks)>();
        foreach (var service in fastDevServices)
        {
            var config = settings.FastDevConfigs.FirstOrDefault(c => c.ServiceKey == service.WatchKey);
            if (config == null) continue;
            var dll = HostDllPath(config);
            before[service.WatchKey] = (dll, HostBuildService.DllMtimeTicks(dll));
        }
        if (before.Count == 0) return;

        AppendLog($"[{DateTime.Now:HH:mm:ss}] 🔨 偵測變動，host 增量 build...");
        var detection = FastDevDetector.Detect(workingDirectory, fastDevServices[0].Name, settings.DefaultRuntimeImage);
        var fallback = before.Keys
            .Select(k => settings.FastDevConfigs.First(c => c.ServiceKey == k))
            .Select(c => Path.Combine(workingDirectory, c.CsprojRelativePath.Replace('/', Path.DirectorySeparatorChar)))
            .ToList();
        var (buildExit, _) = await _hostBuild.BuildAsync(detection.SolutionPath, fallback, AppendLog, CancellationToken.None);
        if (buildExit != 0)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ 增量 build 失敗，容器維持舊 dll (exit {buildExit})");
            return;
        }

        var changedKeys = HostBuildService.ChangedServiceKeys(before, HostBuildService.DllMtimeTicks);
        foreach (var key in changedKeys)
        {
            var service = fastDevServices.First(s => s.WatchKey == key);
            // compose 多半不設 container_name（實際名 <project>-<service>-1），用 monitor 回填的 ContainerId 最保險
            var container = !string.IsNullOrEmpty(service.ContainerId) ? service.ContainerId
                : !string.IsNullOrEmpty(service.ContainerName) ? service.ContainerName
                : service.Name;
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ♻ 重啟 {service.Name}");
            await _dockerCli.RestartContainerAsync(container, AppendLog, CancellationToken.None);
        }
        if (changedKeys.Count > 0) await _monitor.ForceRefreshAsync();
    }

    private FastDevConfig? ResolveFastDevConfig(DockerService service, AppSettings settings)
    {
        var existing = settings.FastDevConfigs.FirstOrDefault(c => c.ServiceKey == service.WatchKey);
        if (existing != null) return existing;

        string? chosen;
        string runtimeImage;

        // 優先照 compose build.dockerfile 確定專案（對齊 VS，零猜測）
        var fromDockerfile = FastDevDetector.FromDockerfile(
            service.WorkingDirectory, service.DockerfilePath, settings.DefaultRuntimeImage);
        if (fromDockerfile != null)
        {
            chosen = fromDockerfile.CsprojRelativePath;
            runtimeImage = fromDockerfile.RuntimeImage;
        }
        else
        {
            var result = FastDevDetector.Detect(service.WorkingDirectory, service.Name, settings.DefaultRuntimeImage);
            chosen = PickBestCsproj(service.Name, result.CsprojCandidates);
            runtimeImage = result.RuntimeImage;
            if (chosen != null && result.CsprojCandidates.Count > 1)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚙ {service.Name} 無 Dockerfile 資訊，自動選用 {chosen}");
        }

        if (chosen == null)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ {service.Name} 找不到對應 .csproj");
            return null;
        }

        var info = FastDevDetector.ReadProjectInfo(service.WorkingDirectory, chosen, "net10.0");
        return new FastDevConfig
        {
            ServiceKey = service.WatchKey,
            CsprojRelativePath = chosen,
            RuntimeImage = runtimeImage,
            Tfm = info.Tfm,
            AssemblyName = info.AssemblyName,
            SrcRoot = service.WorkingDirectory
        };
    }

    // 多個 .csproj 不彈窗打斷：資料夾名與服務名完全相符優先，其次含服務名，再不然取第一個
    private static string? PickBestCsproj(string serviceName, IReadOnlyList<string> candidates)
    {
        if (candidates.Count <= 1) return candidates.Count == 1 ? candidates[0] : null;

        var exact = candidates.FirstOrDefault(c =>
            c.Split('/')[0].Equals(serviceName, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        var contains = candidates.FirstOrDefault(c =>
            c.Split('/')[0].Contains(serviceName, StringComparison.OrdinalIgnoreCase));
        return contains ?? candidates[0];
    }

    private static string HostDllPath(FastDevConfig config)
    {
        var projectDir = FastDevComposeGenerator.ProjectDirRelative(config);
        var baseDir = projectDir.Length == 0
            ? config.SrcRoot
            : Path.Combine(config.SrcRoot, projectDir.Replace('/', Path.DirectorySeparatorChar));
        return Path.Combine(baseDir, "bin", "Debug", config.Tfm, config.AssemblyName + ".dll");
    }

    private (string AppDirHost, string NugetHost) ResolveFastDevHostPaths(FastDevConfig config)
    {
        var projectDir = FastDevComposeGenerator.ProjectDirRelative(config);
        var appHost = projectDir.Length == 0
            ? config.SrcRoot
            : Path.Combine(config.SrcRoot, projectDir.Replace('/', Path.DirectorySeparatorChar));
        var nuget = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        if (DockerCliService.IsWslUncPath(config.SrcRoot))
            return (DockerCliService.ConvertToWslPath(appHost), DockerCliService.ConvertToWslPath(nuget));

        return (appHost, nuget);
    }

    private async Task<bool> IsDotnetSdkAvailableAsync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // 批次操作會用原 image 重建、踩掉 Fast Dev 容器；先提醒並把狀態清乾淨避免顯示與實際不一致
    private async Task<bool> ConfirmBatchOverridesFastDevAsync()
    {
        var fastDevServices = Projects.SelectMany(p => p.ComposeFiles).SelectMany(c => c.Services)
            .Where(s => s.IsFastDev).ToList();
        if (fastDevServices.Count == 0) return true;

        var names = string.Join(", ", fastDevServices.Select(s => s.Name));
        var result = System.Windows.MessageBox.Show(
            $"下列服務正在 Fast Dev：\n{names}\n\n批次操作會用原本 image 重建、踩掉 Fast Dev（狀態會自動關閉）。要繼續嗎？",
            "Fast Dev 提醒", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return false;

        var settings = await _settingsService.LoadAsync();
        foreach (var service in fastDevServices)
        {
            service.IsFastDev = false;
            FastDevOverrideStore.Delete(service.WatchKey);
            PersistFastDev(settings, service.WatchKey, null, enabled: false);
        }
        await _settingsService.SaveAsync(settings);
        _fastDevReload.ClearAll();
        return true;
    }

    internal static void PersistFastDev(AppSettings settings, string serviceKey, FastDevConfig? config, bool enabled)
    {
        settings.FastDevEnabledServiceKeys.Remove(serviceKey);
        settings.FastDevConfigs.RemoveAll(c => c.ServiceKey == serviceKey);
        if (enabled)
        {
            settings.FastDevEnabledServiceKeys.Add(serviceKey);
            if (config != null) settings.FastDevConfigs.Add(config);
        }
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
    private void OpenRepairWindow()
    {
        var window = new Views.DockerRepairWindow(_dockerCli)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var settings = await _settingsService.LoadAsync();
        var settingsWindow = new Views.SettingsWindow(settings, _updateService, info =>
        {
            _pendingUpdate = info;
            UpdateVersion = info.TargetFullRelease.Version.ToString();
            HasUpdate = true;
            ShowUpdateBalloonTip("發現新版本", $"版本 {UpdateVersion} 可更新，點擊工具列「有新版本」按鈕安裝。");
        });
        settingsWindow.Owner = Application.Current.MainWindow;

        if (settingsWindow.ShowDialog() == true)
        {
            await _settingsService.SaveAsync(settings);
            ApplyDockerModeSettings(settings);
            ApplySettings(settings);

            await _monitor.StopAsync();
            _monitor.Start(TimeSpan.FromSeconds(settings.PollIntervalSeconds));
            StatusMessage = "設定已儲存並套用";
        }
    }
}
