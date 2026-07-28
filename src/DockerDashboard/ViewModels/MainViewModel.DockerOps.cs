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

    // 啟動 = Fast Dev（預設就跑最新 code、最快）：host build + 掛 dll + up --build。
    // 範圍跟左側選取走：選了 service 只起該 service、選了專案起該專案，沒選才起全部。
    [RelayCommand]
    private async Task AllUpAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        // 選取範圍決定要起哪些：單一 service > 整個專案 > 全部
        IEnumerable<DockerService> scope =
            SelectedService != null ? [SelectedService]
            : SelectedProject != null ? ProjectServices(SelectedProject)
            : AllServices();

        foreach (var group in scope
                     .Where(s => !s.IsFastDev)
                     .GroupBy(s => s.WorkingDirectory, StringComparer.OrdinalIgnoreCase))
            await EnableFastDevServicesAsync(group.Key, group.ToList());
    }

    [RelayCommand]
    private async Task AllDownAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        if (!await ConfirmBatchOverridesFastDevAsync(AllServices())) return;

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
        if (!await ConfirmBatchOverridesFastDevAsync(ProjectServices(project))) return;

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
        if (!await ConfirmBatchOverridesFastDevAsync(ProjectServices(project))) return;

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

        // 在 Fast Dev 中：完整重建 = 離開 Fast Dev。DisableFastDevServicesAsync 會清狀態、保護同目錄 sibling，並以 up --build 換回正式 image
        if (service.IsFastDev)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 🔨 {service.Name} 離開 Fast Dev，重建正式 image");
            await DisableFastDevServicesAsync([service]);
            return;
        }

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

        var removedFastDev = ProjectServices(project).Where(s => s.IsFastDev).ToList();
        if (removedFastDev.Count > 0)
        {
            var settings = await _settingsService.LoadAsync();
            foreach (var service in removedFastDev)
                PersistFastDev(settings, service.WatchKey, null, enabled: false);
            await _settingsService.SaveAsync(settings);

            foreach (var dir in removedFastDev
                         .Select(s => s.WorkingDirectory)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                FastDevOverrideStore.Delete(dir);
                _fastDevReload.Unwatch(dir);
            }
        }

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

    // 套用最新 code（改了 C#）：已在 Fast Dev 就熱重載，否則先進 Fast Dev（掛最新 dll 起來 = 跑最新 code）
    [RelayCommand]
    private async Task ApplyLatestCodeServiceAsync(DockerService? service)
    {
        if (service == null) return;
        if (service.IsFastDev)
        {
            await OnFastDevSolutionChangedAsync(service.WorkingDirectory);
            return;
        }
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
        // 按下瞬間就給回饋（進度條 + 文字 + log），否則只有按鈕變灰、像沒反應
        IsOperating = true;
        StatusMessage = "⚡ 正在啟用 Fast Dev...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚡ 啟用 Fast Dev（{services.Count} 個服務）...");

        if (!await IsDotnetSdkAvailableAsync())
        {
            IsOperating = false;
            StatusMessage = "⚠ 找不到 host 端 dotnet SDK";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ 找不到 host 端 dotnet SDK，無法啟用 Fast Dev");
            return;
        }

        var settings = await _settingsService.LoadAsync();

        // 偵測含目錄掃描與讀檔，移出 UI 執行緒，否則按鈕按下會卡住數十秒像當機
        StatusMessage = "正在偵測專案（讀取 csproj）...";
        var resolved = await Task.Run(() =>
        {
            // 只掃一次 csproj 共用（僅在有服務缺 build.dockerfile 時才需要），避免每個服務各掃全樹
            var scannedCsproj = services.Any(s => string.IsNullOrEmpty(s.DockerfilePath))
                ? FastDevDetector.EnumerateCsprojRelative(workingDirectory)
                : null;
            var list = new List<(DockerService Service, FastDevConfig Config)>();
            foreach (var service in services)
            {
                var config = ResolveFastDevConfig(service, settings, scannedCsproj);
                if (config == null)
                {
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] ℹ {service.Name} 非 .NET 專案，略過 Fast Dev（仍會照常啟動）");
                    continue;
                }
                list.Add((service, config));
            }
            return list;
        });

        if (resolved.Count == 0)
        {
            IsOperating = false;
            StatusMessage = "⚠ 此專案沒有可啟用 Fast Dev 的 .NET 服務";
            return;
        }

        var enabled = new List<DockerService>();
        foreach (var (service, config) in resolved)
        {
            service.IsFastDev = true;
            PersistFastDev(settings, service.WatchKey, config, enabled: true);
            enabled.Add(service);
        }

        await _settingsService.SaveAsync(settings);
        if (await ApplyFastDevForDirectoryAsync(workingDirectory, settings)) return;

        // build/up 失敗或取消：回滾，避免 UI 與持久化狀態指向不存在的 Fast Dev 容器
        foreach (var service in enabled)
        {
            service.IsFastDev = false;
            PersistFastDev(settings, service.WatchKey, null, enabled: false);
        }
        await _settingsService.SaveAsync(settings);
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ↩ Fast Dev 未套用成功，狀態已回復");

        // 同目錄還有既有 Fast Dev 服務：override/watcher 是共用資源，重套用回先前組態，不能直接砍
        var hasRemaining = AllServices().Any(s => s.IsFastDev &&
            string.Equals(s.WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase));
        if (hasRemaining)
        {
            await ApplyFastDevForDirectoryAsync(workingDirectory, settings);
        }
        else
        {
            FastDevOverrideStore.Delete(workingDirectory);
            _fastDevReload.Unwatch(workingDirectory);
        }
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
    // 回傳是否完整套用成功（取消或任一步失敗 = false），供啟用端決定是否回滾
    private async Task<bool> ApplyFastDevForDirectoryAsync(string workingDirectory, AppSettings settings)
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
                return revertExit == 0 && !ct.IsCancellationRequested;
            }

            var items = new List<(DockerService Service, FastDevConfig Config)>();
            foreach (var service in fastDevServices)
            {
                var config = settings.FastDevConfigs.FirstOrDefault(c => c.ServiceKey == service.WatchKey);
                if (config != null) items.Add((service, config));
            }
            if (items.Count == 0) return false;

            StatusMessage = "正在 host build（首次較久，可取消）...";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 🔨 host build {workingDirectory}");
            var solutionPath = FastDevDetector.FindSolution(workingDirectory);
            var fallbackProjects = items
                .Select(i => Path.Combine(workingDirectory, i.Config.CsprojRelativePath.Replace('/', Path.DirectorySeparatorChar)))
                .ToList();
            var (buildExit, _) = await _hostBuild.BuildAsync(solutionPath, fallbackProjects, AppendLog, ct);
            if (ct.IsCancellationRequested) return false;
            if (buildExit != 0)
            {
                StatusMessage = "⚠ host build 失敗";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ host build 失敗 (exit {buildExit})");
                return false;
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
            if (ct.IsCancellationRequested) return false;
            if (upExit != 0)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ Fast Dev 啟動失敗 (exit {upExit})");
                return false;
            }
            _fastDevReload.Watch(workingDirectory);
            StatusMessage = "✅ Fast Dev 就緒（改 .cs 約 5 秒生效）";
            return true;
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
        var solutionPath = FastDevDetector.FindSolution(workingDirectory);
        var fallback = before.Keys
            .Select(k => settings.FastDevConfigs.First(c => c.ServiceKey == k))
            .Select(c => Path.Combine(workingDirectory, c.CsprojRelativePath.Replace('/', Path.DirectorySeparatorChar)))
            .ToList();
        var (buildExit, _) = await _hostBuild.BuildAsync(solutionPath, fallback, AppendLog, CancellationToken.None);
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

    private FastDevConfig? ResolveFastDevConfig(DockerService service, AppSettings settings, IReadOnlyList<string>? scannedCsproj)
    {
        var existing = settings.FastDevConfigs.FirstOrDefault(c => c.ServiceKey == service.WatchKey);
        if (existing != null) return existing;

        string chosen;
        string runtimeImage;

        if (!string.IsNullOrEmpty(service.DockerfilePath))
        {
            // 有 build.dockerfile：照它確定專案（對齊 VS）。同目錄無 csproj = 非 .NET 服務（如 nginx）→ 跳過，不亂配
            var fromDockerfile = FastDevDetector.FromDockerfile(
                service.WorkingDirectory, service.DockerfilePath, settings.DefaultRuntimeImage);
            if (fromDockerfile == null)
                return null;
            chosen = fromDockerfile.CsprojRelativePath;
            runtimeImage = fromDockerfile.RuntimeImage;
        }
        else
        {
            // 無 Dockerfile 資訊：用預掃的 csproj 清單做「資料夾名 = 服務名」完全比對，找不到就跳過（絕不亂挑別的專案）
            var exact = scannedCsproj?.FirstOrDefault(c =>
                c.Split('/')[0].Equals(service.Name, StringComparison.OrdinalIgnoreCase));
            if (exact == null)
                return null;
            chosen = exact;
            runtimeImage = settings.DefaultRuntimeImage;
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

        // WSL2 模式下 compose 在 WSL 內解析 YAML，Windows 磁碟路徑也要轉 /mnt/<drive>/...
        if (_dockerCli.DockerMode == DockerMode.Wsl2 || DockerCliService.IsWslUncPath(config.SrcRoot))
            return (DockerCliService.ConvertToWslPath(appHost), DockerCliService.ConvertToWslPath(nuget));

        return (appHost, nuget);
    }

    // 快取整個 session：dotnet --version 冷啟動要數秒，且與後面的 host build 是兩次冷啟動；啟動時先暖機一次，按下時就不用等
    internal async Task<bool> IsDotnetSdkAvailableAsync()
    {
        if (_dotnetSdkChecked) return _dotnetSdkAvailable;
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
            _dotnetSdkAvailable = p != null && (await Task.Run(async () => { await p.WaitForExitAsync(); return p.ExitCode; })) == 0;
        }
        catch { _dotnetSdkAvailable = false; }
        _dotnetSdkChecked = true;
        return _dotnetSdkAvailable;
    }

    private List<DockerService> AllServices() =>
        Projects.SelectMany(p => p.ComposeFiles).SelectMany(c => c.Services).ToList();

    private static List<DockerService> ProjectServices(DockerProject project) =>
        project.ComposeFiles.SelectMany(c => c.Services).ToList();

    // 批次操作會用原 image 重建、踩掉 Fast Dev 容器；先提醒並把範圍內狀態清乾淨避免顯示與實際不一致
    private async Task<bool> ConfirmBatchOverridesFastDevAsync(IReadOnlyList<DockerService> scope)
    {
        var fastDevServices = scope.Where(s => s.IsFastDev).ToList();
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
            PersistFastDev(settings, service.WatchKey, null, enabled: false);
        }
        await _settingsService.SaveAsync(settings);

        // override 檔與 watcher 以 WorkingDirectory 為 key，不能拿 WatchKey 刪
        foreach (var dir in fastDevServices.Select(s => s.WorkingDirectory)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            FastDevOverrideStore.Delete(dir);
            _fastDevReload.Unwatch(dir);
        }
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
