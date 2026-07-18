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
        foreach (var service in removedServices.Where(s => s.IsWatching && !DockerCliService.IsWslUncPath(s.WorkingDirectory)))
            _watchService.RemoveWatch(service.WorkingDirectory, service.Name);
        foreach (var dir in removedServices
                     .Where(s => DockerCliService.IsWslUncPath(s.WorkingDirectory))
                     .Select(s => s.WorkingDirectory)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            UpdateComposeWatchForDirectory(dir);

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

        _watchService.ClearAll();
        _composeWatch.ClearAll();
        var settings = await _settingsService.LoadAsync();
        RestoreWatchStateFromSettings(settings);

        await _monitor.ForceRefreshAsync();
        StatusMessage = "重新掃描完成";
    }

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

        if (!_autoWatchEnabled)
            watched = [];

        _composeWatch.SetWatchedServices(workingDirectory, watched);
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
            ApplyWatchSettings(settings);

            var wslDirs = Projects
                .SelectMany(p => p.ComposeFiles)
                .SelectMany(c => c.Services)
                .Where(s => DockerCliService.IsWslUncPath(s.WorkingDirectory))
                .Select(s => s.WorkingDirectory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var dir in wslDirs)
                UpdateComposeWatchForDirectory(dir);

            await _monitor.StopAsync();
            _monitor.Start(TimeSpan.FromSeconds(settings.PollIntervalSeconds));
            StatusMessage = "設定已儲存並套用";
        }
    }
}
