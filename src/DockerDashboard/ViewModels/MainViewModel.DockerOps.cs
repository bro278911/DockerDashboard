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

        var rejected = new List<string>();
        foreach (var folder in dialog.FolderNames)
        {
            if (Projects.Any(p => p.FolderPath.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                continue;

            var error = await AddProjectFromFolderAsync(folder);
            if (error != null)
            {
                rejected.Add($"{folder}\n（{error}）");
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ 未匯入 {folder}：{error}");
                continue;
            }
            RemoveRecentFolder(folder);
        }

        await SaveSettingsAsync();

        if (rejected.Count > 0)
        {
            StatusMessage = $"⚠ {rejected.Count} 個資料夾未匯入";
            System.Windows.MessageBox.Show(
                "以下資料夾未匯入：\n\n" + string.Join("\n\n", rejected) +
                "\n\n請選擇專案根目錄（含 docker-compose.yml 的那一層），" +
                "不要在對話框中點進資料夾後才按「選擇資料夾」。",
                "匯入失敗",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        StatusMessage = $"已匯入 {Projects.Count} 個專案";
    }

    [RelayCommand]
    private async Task ImportRecentFolderAsync(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        if (Projects.Any(p => p.FolderPath.Equals(folder, StringComparison.OrdinalIgnoreCase))) return;

        var error = await AddProjectFromFolderAsync(folder);
        if (error != null)
        {
            StatusMessage = $"⚠ 未匯入 {Path.GetFileName(folder)}：{error}";
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ 未匯入 {folder}：{error}");
            return;
        }

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
        string failMessageFormat)
    {
        if (!TryBeginOperation()) return;
        var cts = _operationCts!;
        var ct = cts.Token;

        IsOperating = true;
        IsCancelling = false;
        StatusMessage = statusMessage;
        BackendLog.Clear();
        AppendLog($"[{DateTime.Now:HH:mm:ss}] {headerLog}");

        var errors = new ConcurrentBag<string>();
        IDisposable? refreshScope = _monitor.SuspendRefreshes();

        var tasks = composeFiles.Select(async compose =>
        {
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

    // 啟動 = 統一流程（StartServicesAsync）：.NET 服務走 Fast Dev（host build + 掛 dll），其餘服務照常 up --build。
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
        List<DockerService> scope =
            SelectedService != null ? [SelectedService]
            : SelectedProject != null ? ProjectServices(SelectedProject)
            : AllServices();

        var targets = scope.Where(ShouldEnableFastDev).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "選取的服務都已在執行";
            return;
        }

        await StartServicesByDirectoryAsync(targets);
    }

    internal static bool ShouldEnableFastDev(DockerService service) =>
        !service.IsFastDev || service.Status is
            ContainerStatus.Stopped or ContainerStatus.Exited or ContainerStatus.Dead;

    // 同一個 compose 目錄的服務一起處理（共用 override、host build 與 up）
    private async Task StartServicesByDirectoryAsync(IEnumerable<DockerService> services)
    {
        var groups = services
            .GroupBy(s => s.WorkingDirectory, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Directory: g.Key, Scope: g.ToList()))
            .ToList();
        if (IsBusy)
        {
            StatusMessage = "⚠ 已有操作進行中，請稍候";
            return;
        }

        // Port 衝突整批只問一次（含會被帶起的依賴）；各目錄內的檢查保留，前面已處理的衝突不會再跳，
        // 只有同批內不同 worktree 互相衝突這類真實新衝突才會再問
        var all = groups.SelectMany(g => g.Scope.Concat(
            ExpandDependencies(g.Scope, ServicesInDirectory(g.Directory)))).ToList();

        // Production 確認要在停止衝突服務之前：拒絕時不能已經動到使用者的服務
        if (!ConfirmProductionStart(all))
        {
            StatusMessage = "已取消以 Production 啟動";
            return;
        }

        if (!await EnsureNoPortConflictAsync(all)) return;

        // 一次取消代表整批取消：前一個目錄被取消就不再處理後續目錄
        _lastOperationCancelled = false;
        foreach (var (directory, scope) in groups)
        {
            await StartServicesAsync(directory, scope, environmentConfirmed: true);
            if (_lastOperationCancelled) break;
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

        if (!await ConfirmBatchOverridesFastDevAsync(AllServices())) return;

        await RunComposeBatchAsync(
            Projects.SelectMany(p => p.ComposeFiles).ToList(),
            (dir, ct) => _dockerCli.ComposeDownWithLogAsync(dir, AppendLog, ct),
            "正在並行停止所有服務...",
            "■ 全部停止（並行）",
            "停止",
            "✅ 所有服務已停止",
            "⚠ {0} 個服務停止失敗");
    }

    [RelayCommand]
    private async Task ProjectUpAsync(DockerProject? project)
    {
        if (project == null) return;
        await StartServicesByDirectoryAsync(ProjectServices(project));
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
            "⚠ {0} 個服務停止失敗");
    }

    [RelayCommand]
    private async Task StartServiceAsync(DockerService? service)
    {
        if (service == null) return;
        await StartServicesAsync(service.WorkingDirectory, [service]);
    }

    [RelayCommand]
    private async Task StopServiceAsync(DockerService? service)
    {
        if (service == null) return;

        if (!TryBeginOperation()) return;
        var cts = _operationCts!;
        var ct = cts.Token;

        IsOperating = true;
        IsCancelling = false;
        StatusMessage = $"正在停止 {service.Name}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ■ 停止 {service.Name}");

        bool wasCancelled = false;
        string? resultMessage = null;
        try
        {
            var (exitCode, _) = await _dockerCli.ComposeStopWithLogAsync(
                service.WorkingDirectory, AppendLog, service.Name, ct);
            if (exitCode != 0)
            {
                resultMessage = $"⚠ {service.Name} 停止失敗";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {service.Name} 停止失敗 (exit code: {exitCode})");
            }
            else
            {
                resultMessage = $"✅ {service.Name} 已停止";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {service.Name} 停止完成");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ {service.Name} 停止已取消");
        }
        catch (Exception ex)
        {
            resultMessage = $"⚠ {service.Name} 停止失敗: {ex.Message}";
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

        StatusMessage = wasCancelled ? "⏹ 操作已取消" : resultMessage ?? StatusMessage;
    }

    // 容器在跑一律 docker restart（Fast Dev 靠掛載的 dll、一般服務靠既有 image，都不需重建）；沒在跑則走統一啟動
    [RelayCommand]
    private async Task RestartServiceAsync(DockerService? service)
    {
        if (service == null) return;
        if (!service.IsRunning)
        {
            await StartServicesAsync(service.WorkingDirectory, [service]);
            return;
        }

        await RunOperationAsync(
            $"正在重啟 {service.Name}...",
            $"🔄 重啟 {service.Name}",
            async ct =>
            {
                var (exitCode, _) = await _dockerCli.RestartContainerAsync(ContainerRef(service), AppendLog, ct);
                if (exitCode != 0)
                {
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {service.Name} 重啟失敗 (exit code: {exitCode})");
                    return $"⚠ {service.Name} 重啟失敗";
                }
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {service.Name} 重啟完成");
                return $"✅ {service.Name} 已重啟";
            });
    }

    // compose 多半不設 container_name（實際名 <project>-<service>-1），用 monitor 回填的 ContainerId 最保險
    private static string ContainerRef(DockerService service) =>
        !string.IsNullOrEmpty(service.ContainerId) ? service.ContainerId
        : !string.IsNullOrEmpty(service.ContainerName) ? service.ContainerName
        : service.Name;

    // 完整重建：只動該服務，用正式 image（離開 Fast Dev）重新 build 並啟動
    [RelayCommand]
    private async Task RebuildRestartServiceAsync(DockerService? service)
    {
        if (service == null) return;

        if (service.IsFastDev && !ConfirmLeaveFastDev(service))
            return;

        await StartServicesAsync(service.WorkingDirectory, [service], exitFastDev: true);
    }

    internal Func<DockerService, bool> ConfirmLeaveFastDev { get; set; } = service =>
        System.Windows.MessageBox.Show(
            $"{service.Name} 正在 Fast Dev。\n\n完整重建會離開 Fast Dev、改用完整 image（重新 build，分鐘級）。要繼續嗎？",
            "完整重建", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
        == System.Windows.MessageBoxResult.Yes;


    [RelayCommand]
    private async Task RemoveProjectAsync(DockerProject? project)
    {
        if (project == null) return;

        var runningComposes = project.ComposeFiles
            .Where(c => c.Services.Any(s => s.IsRunning))
            .ToList();
        if (runningComposes.Count > 0 && IsDockerAvailable)
        {
            if (!TryBeginOperation()) return;
            var cts = _operationCts!;
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

        TrackRecentlyRemovedFolder(project.FolderPath);

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
                _pendingReloadDirs.Remove(dir);
            }
        }

        // 環境設定跟著移除，否則再匯入顯示 Development、重開 App 卻變回舊環境
        await _settingsService.UpdateAsync(s =>
            ProjectEnvironment.Set(s.ProjectEnvironments, project.FolderPath, ProjectEnvironment.Development));
        await SaveSettingsAsync();
        UpdateCounts();
        StatusMessage = $"已移除 {project.Name}";
    }

    /// <summary>把掃描結果套進既有專案物件，保留參照身分（供 Rescan 使用）</summary>
    private static void ApplyScanResult(DockerProject target, DockerProject scanned)
    {
        target.Name = scanned.Name;
        target.IsGitRepo = scanned.IsGitRepo;
        target.CurrentBranch = scanned.CurrentBranch;
        target.IsDirty = scanned.IsDirty;

        target.ComposeFiles.Clear();
        foreach (var composeFile in scanned.ComposeFiles)
            target.ComposeFiles.Add(composeFile);
    }

    [RelayCommand]
    private async Task RescanProjectsAsync()
    {
        // 記住掃描開始時的物件參照，供稍後判斷哪些是「掃描期間才新匯入」的
        var originals = Projects.ToList();
        var folders = originals.Select(p => p.FolderPath).ToList();

        // 先在暫存集合掃描完，成功才整批換進 Projects：直接 Clear 再 await 的話，
        // 期間其他命令（如前端專案增修）觸發存檔會把 ImportedFolders 寫成空清單；
        // 掃描中途拋例外更會讓 Projects 永久停在空的狀態
        List<DockerProject> results;
        try
        {
            results = [.. await Task.WhenAll(
                folders.Where(Directory.Exists).Select(f => BuildProjectAsync(f, useCache: false)))];
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ 重新掃描失敗，保留原有專案清單: {ex.Message}");
            StatusMessage = "⚠ 重新掃描失敗";
            return;
        }

        // 依「掃描結束當下」的清單合併，不是拿掃描開始時的舊快照整批覆蓋：
        // 掃描期間使用者可能匯入新專案（會被舊快照抹掉）或移除專案（會被舊快照復活）。
        // 用物件參照而非路徑判斷是否為掃描期間新增，否則「資料夾已不存在、因此沒有掃描結果」
        // 的失效專案會被誤認成新匯入而永久保留，失去重新掃描本該有的清除效果
        var scannedByFolder = results.ToDictionary(
            p => p.FolderPath, StringComparer.OrdinalIgnoreCase);

        // 保留原物件參照、只更新其內容：換成新實例的話，掃描期間正在進行的「移除專案」
        // 拿的是舊參照，Projects.Remove 會失敗，使用者要求刪掉的專案還留在清單上
        var stale = new List<DockerProject>();
        foreach (var existing in Projects)
        {
            if (!originals.Any(o => ReferenceEquals(o, existing)))
                continue; // 掃描期間新匯入，原樣保留

            if (scannedByFolder.TryGetValue(existing.FolderPath, out var rescanned))
            {
                ApplyScanResult(existing, rescanned);
                if (existing.ComposeFiles.Count == 0)
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ {existing.Name} 未偵測到服務（docker compose config 可能失敗）");
            }
            else
            {
                stale.Add(existing); // 資料夾已失效
            }
        }

        foreach (var project in stale)
        {
            Projects.Remove(project);
            TrackRecentlyRemovedFolder(project.FolderPath);
        }

        if (stale.Count > 0)
        {
            var staleFolders = stale
                .Select(project => project.FolderPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            await _settingsService.UpdateAsync(settings =>
            {
                settings.ImportedFolders =
                    [.. settings.ImportedFolders.Where(folder => !staleFolders.Contains(folder))];
                settings.RecentlyRemovedFolders = [.. RecentlyRemovedFolders];
            });
        }

        _ = RefreshAvailableEnvironmentsAsync();
        _fastDevReload.ClearAll();
        // 先刷新容器狀態再還原旗標：新掃出的服務狀態是 Unknown，先還原會把在跑的 Fast Dev 旗標誤清
        var statusConfirmed = await _monitor.ForceRefreshAsync();
        var settings = await _settingsService.LoadAsync();
        RestoreFastDevStateFromSettings(settings, statusConfirmed);
        StatusMessage = "重新掃描完成";
    }

    // 套用最新 code（改了 C#）：Fast Dev 且在跑 → host build + 一定重啟該服務；其餘走統一啟動
    [RelayCommand]
    private async Task ApplyLatestCodeServiceAsync(DockerService? service)
    {
        if (service == null) return;
        if (service.IsFastDev && service.IsRunning)
        {
            if (IsBusy)
            {
                StatusMessage = "⚠ 已有操作進行中，請稍候";
                return;
            }
            await OnFastDevSolutionChangedAsync(service.WorkingDirectory, service);
            return;
        }
        await StartServicesAsync(service.WorkingDirectory, [service]);
    }

    [RelayCommand]
    private async Task ToggleProjectFastDevAsync(DockerProject? project)
    {
        if (project == null)
        {
            StatusMessage = "⚠ 請先在左側選一個專案";
            return;
        }
        var services = ProjectServices(project);
        if (services.Any(s => s.IsFastDev))
        {
            // 離開 Fast Dev：只還原原本在 Fast Dev 的服務，其他服務不動
            _lastOperationCancelled = false;
            foreach (var group in services.Where(s => s.IsFastDev)
                         .GroupBy(s => s.WorkingDirectory, StringComparer.OrdinalIgnoreCase))
            {
                await StartServicesAsync(group.Key, group.ToList(), exitFastDev: true);
                if (_lastOperationCancelled) break;
            }
            return;
        }
        await StartServicesByDirectoryAsync(services);
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
        {
            if (IsBusy)
            {
                StatusMessage = "⚠ 已有操作進行中，請稍候";
                return;
            }
            await OnFastDevSolutionChangedAsync(dir);
        }
    }

    // 最近一次 RunOperationAsync 是否被使用者取消；批次迴圈據此在取消後停止處理後續目錄
    private bool _lastOperationCancelled;

    // 單一操作鎖＋進度＋取消＋收尾。work 回傳完成後要顯示的訊息（null = 不覆寫狀態列）。
    // 訊息延後到 ForceRefreshAsync 刷新容器清單「之後」才顯示，避免容器列表還沒轉為執行中就看到「已啟動」。
    // 回傳是否取得了操作鎖
    private async Task<bool> RunOperationAsync(string startStatus, string startLog, Func<CancellationToken, Task<string?>> work)
    {
        if (!TryBeginOperation()) return false;
        var cts = _operationCts!;
        var ct = cts.Token;

        IsOperating = true;
        IsCancelling = false;
        StatusMessage = startStatus;
        AppendLog($"[{DateTime.Now:HH:mm:ss}] {startLog}");

        bool wasCancelled;
        string? resultMessage = null;
        try
        {
            resultMessage = await work(ct);
        }
        catch (OperationCanceledException)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ 操作已取消");
        }
        catch (Exception ex)
        {
            resultMessage = $"⚠ 操作失敗: {ex.Message}";
            AppendLog($"[例外] {ex.Message}");
        }
        finally
        {
            wasCancelled = cts.IsCancellationRequested;
            _lastOperationCancelled = wasCancelled;
            if (ReferenceEquals(_operationCts, cts))
                _operationCts = null;
            cts.Dispose();
            await _monitor.ForceRefreshAsync();
            IsCancelling = false;
            IsOperating = false;
        }

        StatusMessage = wasCancelled ? "⏹ 操作已取消" : resultMessage ?? StatusMessage;
        return true;
    }

    // 統一的服務啟動流程：所有「啟動／套用最新 code／完整重建／離開 Fast Dev」都走這裡。
    // - 只 up scope 內的服務（--no-deps），不連帶重建依賴；
    // - override 以目錄內「所有」Fast Dev 服務產生，scope 外既有的 Fast Dev 服務不會被換回原 image；
    // - 解析不到 FastDevConfig 的服務（nginx 等非 .NET）照常列入 up，只是不進 Fast Dev；
    // - exitFastDev=true：清掉 scope 內服務的 Fast Dev 旗標後以正式 image 啟動（不 host build）。
    // 回傳是否完整成功（取消、失敗、被其他操作擋下皆為 false）
    internal async Task<bool> StartServicesAsync(
        string workingDirectory, IReadOnlyList<DockerService> scope, bool exitFastDev = false,
        bool environmentConfirmed = false)
    {
        if (scope.Count == 0) return true;
        if (IsBusy)
        {
            StatusMessage = "⚠ 已有操作進行中，請稍候";
            return false;
        }

        // Production 啟動保護：拒絕就中止，不改任何狀態（熱重載不走這裡，不重問）
        if (!exitFastDev && !environmentConfirmed && !ConfirmProductionStart(scope))
        {
            StatusMessage = "已取消以 Production 啟動";
            return false;
        }

        // 單服務啟動時，把未執行的依賴一併帶起（nginx 等反向代理缺後端會直接起不來）；
        // 完整重建／離開 Fast Dev 只動指定服務，不展開
        if (!exitFastDev)
        {
            var added = ExpandDependencies(scope, ServicesInDirectory(workingDirectory));
            if (added.Count > 0)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ➕ 一併帶起未執行的依賴：{string.Join(", ", added.Select(s => s.Name))}");
                scope = [.. scope, .. added];
            }
        }

        if (!await EnsureNoPortConflictAsync(scope)) return false;

        var success = false;
        var secrets = new List<string>();
        var names = string.Join(", ", scope.Select(s => s.Name));
        var started = await RunOperationAsync(
            exitFastDev ? "正在以正式 image 啟動..." : "正在啟動...",
            exitFastDev ? $"🔨 以正式 image 啟動 {names}（離開 Fast Dev）" : $"▶ 啟動 {names}",
            async ct =>
            {
                var idsBefore = scope.Distinct().ToDictionary(s => s, s => s.ContainerId);
                var appliedEnvironments = new List<(DockerService Service, string Environment)>();
                var upAttempted = false;
                var settings = await _settingsService.LoadAsync();
                var enabled = new List<DockerService>();
                var exited = new List<(DockerService Service, FastDevConfig? Config)>();
                var resolved = new List<(DockerService Service, FastDevConfig Config)>();
                try
                {
                    if (exitFastDev)
                    {
                        foreach (var service in scope.Where(s => s.IsFastDev))
                        {
                            exited.Add((service, settings.FastDevConfigs.FirstOrDefault(c => c.ServiceKey == service.WatchKey)));
                            service.IsFastDev = false;
                            PersistFastDev(settings, service.WatchKey, null, enabled: false);
                        }
                        await SaveFastDevStateAsync(settings, exited.Select(e => (e.Service.WatchKey, (FastDevConfig?)null, false)));
                    }
                    else
                    {
                        // 偵測含目錄掃描與讀檔，移出 UI 執行緒，否則按鈕按下會卡住數十秒像當機
                        StatusMessage = "正在偵測專案（讀取 csproj）...";
                        resolved = await Task.Run(() => ResolveScopeConfigs(workingDirectory, scope, settings));

                        // 沒有 .NET 服務就不需要 host SDK，直接照常 up
                        if (resolved.Count > 0 && !await IsDotnetSdkAvailableAsync())
                        {
                            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ 找不到 host 端 dotnet SDK，無法啟用 Fast Dev");
                            return "⚠ 找不到 host 端 dotnet SDK";
                        }

                        foreach (var (service, config) in resolved)
                        {
                            if (!service.IsFastDev) enabled.Add(service);
                            service.IsFastDev = true;
                            PersistFastDev(settings, service.WatchKey, config, enabled: true);
                        }
                        if (resolved.Count > 0)
                            await SaveFastDevStateAsync(settings, resolved.Select(r => (r.Service.WatchKey, (FastDevConfig?)r.Config, true)));
                    }

                    // 只 build scope 內的 .NET 服務專案（MSBuild 增量，scope 外的專案不動）
                    if (resolved.Count > 0)
                    {
                        StatusMessage = "正在 host build（首次較久，可取消）...";
                        AppendLog($"[{DateTime.Now:HH:mm:ss}] 🔨 host build {workingDirectory}");
                        var projects = resolved
                            .Select(i => Path.Combine(workingDirectory,
                                i.Config.CsprojRelativePath.Replace('/', Path.DirectorySeparatorChar)))
                            .ToList();
                        var (buildExit, _) = await _hostBuild.BuildAsync(
                            FastDevDetector.FindSolution(workingDirectory), projects, AppendLog, ct);
                        ct.ThrowIfCancellationRequested();
                        if (buildExit != 0)
                        {
                            AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ host build 失敗 (exit {buildExit})");
                            return "⚠ host build 失敗";
                        }
                    }

                    // override 與成功後記錄的套用環境必須是同一份值：up 可能跑數分鐘，期間專案環境可能被改
                    appliedEnvironments = resolved.Select(r => (r.Service, EnvironmentOf(r.Service))).ToList();
                    var overridePath = await SyncFastDevOverrideAsync(workingDirectory, settings, secrets);

                    StatusMessage = "正在啟動容器...";
                    upAttempted = true;
                    var upNames = scope.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var (upExit, _) = await _dockerCli.ComposeUpServicesAsync(
                        workingDirectory, upNames, line => AppendLog(RedactSecrets(line, secrets)), ct, overridePath);
                    ct.ThrowIfCancellationRequested();
                    if (upExit != 0)
                    {
                        AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ 啟動失敗 (exit {upExit})");
                        return $"⚠ 啟動失敗 (exit {upExit})";
                    }

                    success = true;
                    // 記錄這批服務容器實際套用的環境：離開 Fast Dev 的回到一般 image（等同 Development，移除記錄）
                    await SaveAppliedEnvironmentsAsync(exitFastDev
                        ? exited.Select(e => (e.Service, ProjectEnvironment.Development))
                        : appliedEnvironments);
                    foreach (var project in scope.Select(ProjectOf).OfType<DockerProject>().Distinct())
                        RefreshEnvironmentPending(project);
                    if (overridePath != null) _fastDevReload.Watch(workingDirectory);
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {names} 啟動完成");
                    var restartNote = await RestartDependentsOfRebuiltAsync(workingDirectory, scope, idsBefore, ct);
                    if (exitFastDev) return "✅ 已離開 Fast Dev" + restartNote;
                    return (resolved.Count > 0 ? "✅ 已啟動（Fast Dev，改 .cs 後約 20 到 45 秒生效）" : $"✅ {names} 已啟動") + restartNote;
                }
                finally
                {
                    if (!success && enabled.Count + exited.Count > 0)
                        await RollbackFastDevAsync(workingDirectory, settings, enabled, resolved, exited);
                    // up 已送出卻失敗／取消：compose 可能已用新環境換掉部分容器，無法確定實際套用的環境，
                    // 標為 Unknown，讓待重啟持續顯示、下次切換環境一定重啟這些服務
                    if (!success && upAttempted && !exitFastDev)
                    {
                        await SaveAppliedEnvironmentsAsync(appliedEnvironments
                            .Where(a => a.Service.IsFastDev)
                            .Select(a => (a.Service, ProjectEnvironment.Unknown)));
                        foreach (var project in scope.Select(ProjectOf).OfType<DockerProject>().Distinct())
                            RefreshEnvironmentPending(project);
                    }
                }
            });

        return started && success;
    }

    // 失敗／取消：依容器實際狀態校正本次新啟用與本次離開的 Fast Dev 旗標與持久化。
    // compose up 是批次操作，失敗時部分服務可能已換好容器，盲目整批還原會讓旗標與實際脫鉤：
    // 容器 image 是 <服務>:fastdev 視為 Fast Dev，否則視為一般 image；查不到容器（或取不到清單）才沿用舊規則
    private async Task RollbackFastDevAsync(
        string workingDirectory, AppSettings settings, IReadOnlyList<DockerService> enabled,
        IReadOnlyList<(DockerService Service, FastDevConfig Config)> resolved,
        IReadOnlyList<(DockerService Service, FastDevConfig? Config)> exited)
    {
        // 取消時原 token 已失效，校正用的查詢不能沿用
        var containers = await _dockerCli.GetRunningContainersAsync(CancellationToken.None);
        var actual = containers == null
            ? new Dictionary<DockerService, ContainerInfo?>()
            : ResolveContainers(containers, [.. enabled, .. exited.Select(e => e.Service)]);

        var changes = new List<(string Key, FastDevConfig? Config, bool Enabled)>();
        foreach (var service in enabled)
        {
            var isFastDev = IsFastDevContainer(service, actual) ?? false;
            service.IsFastDev = isFastDev;
            changes.Add((service.WatchKey, isFastDev ? resolved.First(r => r.Service == service).Config : null, isFastDev));
        }
        foreach (var (service, config) in exited)
        {
            var isFastDev = IsFastDevContainer(service, actual) ?? true;
            service.IsFastDev = isFastDev;
            changes.Add((service.WatchKey, isFastDev ? config : null, isFastDev));
        }

        await SaveFastDevStateAsync(settings, changes);
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ↩ Fast Dev 未套用成功，狀態已依容器實際狀態校正");
        // 同目錄仍有 Fast Dev：override/watcher 是共用資源，重寫回校正後的組態，不重 up；
        // 離開 Fast Dev 時 watcher 可能已被卸除，要一併裝回
        if (await SyncFastDevOverrideAsync(workingDirectory, settings) != null)
            _fastDevReload.Watch(workingDirectory);
    }

    // null = 找不到該服務的容器，無從判斷
    private static bool? IsFastDevContainer(DockerService service, IReadOnlyDictionary<DockerService, ContainerInfo?> actual) =>
        actual.TryGetValue(service, out var container) && container != null
            ? string.Equals(container.Image, $"{service.Name}:fastdev", StringComparison.OrdinalIgnoreCase)
            : null;

    // 只合併套用環境欄位（同 SaveFastDevStateAsync 的理由）；Development 是預設值不存
    private Task SaveAppliedEnvironmentsAsync(IEnumerable<(DockerService Service, string Environment)> changes)
    {
        var list = changes.ToList();
        foreach (var (service, environment) in list)
            service.AppliedEnvironment = environment;
        if (list.Count == 0) return Task.CompletedTask;

        return _settingsService.UpdateAsync(latest =>
        {
            foreach (var (service, environment) in list)
            {
                latest.AppliedEnvironments.Remove(service.WatchKey);
                if (environment != ProjectEnvironment.Development)
                    latest.AppliedEnvironments[service.WatchKey] = environment;
            }
        });
    }

    // 只合併 Fast Dev 欄位：操作可能跑數分鐘，整份存回開頭的快照會蓋掉期間其他命令寫入的設定
    private Task SaveFastDevStateAsync(
        AppSettings settings, IEnumerable<(string Key, FastDevConfig? Config, bool Enabled)> changes)
    {
        var list = changes.ToList();
        foreach (var (key, config, enabled) in list)
            PersistFastDev(settings, key, config, enabled);
        return _settingsService.UpdateAsync(latest =>
        {
            foreach (var (key, config, enabled) in list)
                PersistFastDev(latest, key, config, enabled);
        });
    }

    private List<DockerService> ServicesInDirectory(string workingDirectory) =>
        AllServices()
            .Where(s => string.Equals(s.WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase))
            .ToList();

    // 沿 depends_on 遞迴找出 scope 需要一併帶起的服務：只加 !IsRunning 的；已在跑的不加、不動，但遞迴仍穿過它繼續看。
    // visited 兼防循環依賴；依賴名稱在同目錄找不到就略過
    internal static List<DockerService> ExpandDependencies(
        IReadOnlyList<DockerService> scope, IReadOnlyList<DockerService> sameDirectory)
    {
        var byName = new Dictionary<string, DockerService>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in sameDirectory)
            byName.TryAdd(service.Name, service);

        var visited = new HashSet<string>(scope.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        var added = new List<DockerService>();
        var pending = new Queue<DockerService>(scope);
        while (pending.TryDequeue(out var current))
        {
            foreach (var dependency in current.DependsOn)
            {
                if (!visited.Add(dependency) || !byName.TryGetValue(dependency, out var service)) continue;
                if (!service.IsRunning) added.Add(service);
                pending.Enqueue(service);
            }
        }
        return added;
    }

    // ID 不同視為容器被重建；任一邊拿不到 ID（從沒有過容器、或比對不到）無從比較，不算重建
    internal static List<DockerService> DetectRebuilt(
        IReadOnlyDictionary<DockerService, string> idsBefore, IReadOnlyDictionary<DockerService, string> idsAfter) =>
        idsBefore
            .Where(kv => !string.IsNullOrEmpty(kv.Value)
                         && idsAfter.TryGetValue(kv.Key, out var after)
                         && !string.IsNullOrEmpty(after)
                         && after != kv.Value)
            .Select(kv => kv.Key)
            .ToList();

    // 同目錄、仍在跑、本身沒被重建，且直接或間接依賴被重建服務者：nginx 的 proxy_pass 寫死主機名，
    // 後端容器重建 IP 變動後必須重啟才會重新解析。scope 內但這次沒被重建的（compose 判定無變更）同樣要重啟
    internal static List<DockerService> FindDependentsToRestart(
        IReadOnlyList<DockerService> sameDirectory, IReadOnlyList<DockerService> rebuilt)
    {
        var affected = new HashSet<string>(rebuilt.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        bool grew;
        do
        {
            grew = false;
            foreach (var service in sameDirectory)
            {
                if (!affected.Contains(service.Name) && service.DependsOn.Any(affected.Contains))
                {
                    affected.Add(service.Name);
                    grew = true;
                }
            }
        } while (grew);

        var rebuiltSet = new HashSet<DockerService>(rebuilt);
        return sameDirectory
            .Where(s => !rebuiltSet.Contains(s) && s.IsRunning && affected.Contains(s.Name))
            .ToList();
    }

    // up 成功後比對容器 ID，重啟依賴「被重建服務」的服務。失敗只警告、不影響啟動結果；回傳要附在狀態訊息的註記
    private async Task<string> RestartDependentsOfRebuiltAsync(
        string workingDirectory, IReadOnlyList<DockerService> scope,
        IReadOnlyDictionary<DockerService, string> idsBefore, CancellationToken ct)
    {
        var containers = await _dockerCli.GetRunningContainersAsync(ct);
        if (containers == null) return string.Empty;

        var rebuilt = DetectRebuilt(idsBefore, ResolveContainerIds(containers, scope));
        if (rebuilt.Count == 0) return string.Empty;

        var targets = FindDependentsToRestart(ServicesInDirectory(workingDirectory), rebuilt);
        if (targets.Count == 0) return string.Empty;

        var results = await Task.WhenAll(targets.Select(async service =>
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ♻ 重啟 {service.Name}（依賴的容器已重建）");
            try
            {
                var (exitCode, _) = await _dockerCli.RestartContainerAsync(ContainerRef(service), AppendLog, ct);
                return (service.Name, Ok: exitCode == 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppendLog($"[例外] {ex.Message}");
                return (service.Name, Ok: false);
            }
        }));

        var failed = results.Where(r => !r.Ok).Select(r => r.Name).ToList();
        foreach (var name in failed)
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ {name} 重啟失敗，請手動重啟以套用新容器位址");
        return failed.Count > 0 ? $"（⚠ 重啟失敗：{string.Join(", ", failed)}）" : string.Empty;
    }

    private Dictionary<DockerService, string> ResolveContainerIds(
        List<ContainerInfo> containers, IReadOnlyList<DockerService> services) =>
        ResolveContainers(containers, services)
            .ToDictionary(kv => kv.Key, kv => kv.Value?.ID ?? string.Empty);

    private Dictionary<DockerService, ContainerInfo?> ResolveContainers(
        List<ContainerInfo> containers, IReadOnlyList<DockerService> services)
    {
        var (matcher, ambiguousNames) = CreateMatcher(containers);
        var wanted = new HashSet<DockerService>(services);
        var matches = new Dictionary<DockerService, ContainerInfo?>();
        foreach (var compose in Projects.SelectMany(p => p.ComposeFiles))
        {
            foreach (var service in compose.Services.Where(wanted.Contains))
            {
                matches[service] = matcher.Resolve(
                    service.Name, service.ContainerName, service.WorkingDirectory,
                    compose.ProjectName, ambiguousNames);
            }
        }
        return matches;
    }

    private List<(DockerService Service, FastDevConfig Config)> ResolveScopeConfigs(
        string workingDirectory, IReadOnlyList<DockerService> scope, AppSettings settings)
    {
        // 只掃一次 csproj 共用（且只在真的要走資料夾名比對時才掃），避免每個服務各掃全樹
        var scannedCsproj = new Lazy<IReadOnlyList<string>>(
            () => FastDevDetector.EnumerateCsprojRelative(workingDirectory));
        var list = new List<(DockerService Service, FastDevConfig Config)>();
        foreach (var service in scope)
        {
            var config = ResolveFastDevConfig(service, settings, scannedCsproj, out var skipReason);
            if (config == null)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ℹ {service.Name} 非 .NET 服務，以一般 image 啟動（{skipReason}）");
                continue;
            }
            list.Add((service, config));
        }
        return list;
    }

    // 依目錄內「所有」Fast Dev 服務重寫 override 並回傳路徑；已無 Fast Dev 服務則刪 override、停止監看並回傳 null
    // secretsOut：收集本次寫進 override 的憑證值，供呼叫端遮蔽 compose 輸出
    // 憑證解析要讀檔、必要時掃整個目錄樹，組裝與寫檔都移到背景執行緒；UI 相關狀態（服務、環境、路徑）先在此擷取
    private async Task<string?> SyncFastDevOverrideAsync(
        string workingDirectory, AppSettings settings, ICollection<string>? secretsOut = null)
    {
        var items = AllServices()
            .Where(s => s.IsFastDev &&
                        string.Equals(s.WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase))
            .Select(s => (Service: s, Config: settings.FastDevConfigs.FirstOrDefault(c => c.ServiceKey == s.WatchKey)))
            .Where(i => i.Config != null)
            .Select(i =>
            {
                var (appDirHost, nugetHost) = ResolveFastDevHostPaths(i.Config!);
                return (Name: i.Service.Name, Config: i.Config!, AppDirHost: appDirHost, NugetHost: nugetHost,
                    ProjectDir: HostProjectDir(i.Config!), Environment: EnvironmentOf(i.Service));
            })
            .ToList();

        if (items.Count == 0)
        {
            FastDevOverrideStore.Delete(workingDirectory);
            _fastDevReload.Unwatch(workingDirectory);
            return null;
        }

        var (path, secrets, warnings) = await Task.Run(() =>
        {
            var warnings = new List<string>();
            var secrets = new List<string>();
            var fallbackDirs = new Lazy<List<string>>(() => EnvironmentCredentialResolver.EnumerateFallbackDirs(workingDirectory));
            var blocks = items.Select(i =>
            {
                AzureCredential? credential = null;
                if (i.Environment != ProjectEnvironment.Development)
                {
                    credential = EnvironmentCredentialResolver.Resolve(
                        i.ProjectDir, () => fallbackDirs.Value, i.Environment,
                        message => warnings.Add($"[{DateTime.Now:HH:mm:ss}] ⚠ {i.Name}（{i.Environment}）：{message}"));
                    if (credential != null)
                        secrets.AddRange(FastDevComposeGenerator.SecretForms(credential.ClientSecret));
                }
                return FastDevComposeGenerator.ServiceOverrideBlock(
                    i.Name, i.Config, i.AppDirHost, i.NugetHost, i.Environment, credential);
            }).ToList();
            return (FastDevOverrideStore.Write(workingDirectory, FastDevComposeGenerator.CombineOverride(blocks)), secrets, warnings);
        });

        foreach (var warning in warnings) AppendLog(warning);
        if (secretsOut != null)
            foreach (var secret in secrets) secretsOut.Add(secret);
        return path;
    }

    // 熱重載：host 增量 build 目錄內所有 Fast Dev 服務（只有受影響專案的 dll 會變），重啟 dll 有變動的服務，
    // 另加 forceRestart（使用者明確要求套用最新 code 的那個服務，即使 dll 沒變也重啟）。
    // 已有操作進行中不可插隊：watcher 觸發（forceRestart 為 null）的記下目錄，操作結束後由 OnIsOperatingChanged 補跑；
    // 使用者明確指定服務的呼叫則直接略過
    internal async Task OnFastDevSolutionChangedAsync(string workingDirectory, DockerService? forceRestart = null)
    {
        if (IsBusy)
        {
            if (forceRestart == null)
            {
                _pendingReloadDirs.Add(workingDirectory);
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏭ 已有操作進行中，操作結束後補跑熱重載");
            }
            else
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏭ 已有操作進行中，略過熱重載");
            return;
        }

        await RunOperationAsync("正在熱重載...", "🔨 host 增量 build...", async ct =>
        {
            var settings = await _settingsService.LoadAsync();
            var fastDevServices = AllServices()
                .Where(s => s.IsFastDev &&
                            string.Equals(s.WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (fastDevServices.Count == 0) return null;

            var before = new Dictionary<string, (string DllPath, long BeforeTicks)>();
            var configs = new List<FastDevConfig>();
            foreach (var service in fastDevServices)
            {
                var config = settings.FastDevConfigs.FirstOrDefault(c => c.ServiceKey == service.WatchKey);
                if (config == null) continue;
                configs.Add(config);
                var dll = HostDllPath(config);
                before[service.WatchKey] = (dll, HostBuildService.DllMtimeTicks(dll));
            }
            if (before.Count == 0) return null;

            var fallback = configs
                .Select(c => Path.Combine(workingDirectory, c.CsprojRelativePath.Replace('/', Path.DirectorySeparatorChar)))
                .ToList();
            var (buildExit, _) = await _hostBuild.BuildAsync(
                FastDevDetector.FindSolution(workingDirectory), fallback, AppendLog, ct);
            ct.ThrowIfCancellationRequested();
            if (buildExit != 0)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ 增量 build 失敗，容器維持舊 dll (exit {buildExit})");
                return "⚠ 增量 build 失敗，容器維持舊 dll";
            }

            var restartKeys = HostBuildService.ChangedServiceKeys(before, HostBuildService.DllMtimeTicks).ToList();
            if (forceRestart != null && !restartKeys.Contains(forceRestart.WatchKey))
                restartKeys.Add(forceRestart.WatchKey);
            if (restartKeys.Count == 0) return "✅ 沒有變動，不需重啟";

            var targets = fastDevServices.Where(s => restartKeys.Contains(s.WatchKey)).ToList();
            var results = await Task.WhenAll(targets.Select(async service =>
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ♻ 重啟 {service.Name}");
                var (exitCode, _) = await _dockerCli.RestartContainerAsync(ContainerRef(service), AppendLog, ct);
                return (service.Name, exitCode);
            }));

            var failed = results.Where(r => r.exitCode != 0).Select(r => r.Name).ToList();
            foreach (var (name, exitCode) in results.Where(r => r.exitCode != 0))
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {name} 重啟失敗 (exit code: {exitCode})");
            return failed.Count > 0
                ? $"⚠ 重啟失敗：{string.Join(", ", failed)}"
                : $"✅ 已熱重載 {targets.Count} 個服務";
        });
    }

    // 操作進行中被擋下的熱重載目錄；只在 UI 執行緒存取
    private readonly HashSet<string> _pendingReloadDirs = new(StringComparer.OrdinalIgnoreCase);

    partial void OnIsOperatingChanged(bool value)
    {
        if (!value) _ = DrainPendingReloadsAsync();
    }

    // 每次只補跑一個目錄，該次操作結束會再觸發下一輪排空
    private async Task DrainPendingReloadsAsync()
    {
        try
        {
            // 讓呼叫端先寫完結果訊息並釋放操作鎖
            await Task.Yield();
            if (IsBusy) return;

            while (_pendingReloadDirs.Count > 0)
            {
                var directory = _pendingReloadDirs.First();
                _pendingReloadDirs.Remove(directory);
                // 排隊期間專案可能已移除或離開 Fast Dev
                if (!ServicesInDirectory(directory).Any(s => s.IsFastDev)) continue;

                await OnFastDevSolutionChangedAsync(directory);
                return;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ 補跑熱重載失敗: {ex.Message}");
        }
    }

    // 從背景執行緒回呼時切回 UI 執行緒；沒有 Dispatcher（測試）或已在 UI 執行緒就直接執行
    private static Task RunOnUiThreadAsync(Func<Task> work)
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher == null || dispatcher.CheckAccess()
            ? work()
            : dispatcher.InvokeAsync(work).Task.Unwrap();
    }

    private FastDevConfig? ResolveFastDevConfig(
        DockerService service, AppSettings settings, Lazy<IReadOnlyList<string>> scannedCsproj, out string skipReason)
    {
        skipReason = string.Empty;
        var existing = settings.FastDevConfigs.FirstOrDefault(c => c.ServiceKey == service.WatchKey);
        if (existing != null) return existing;

        string? chosen = null;
        string? runtimeImage = null;

        if (!string.IsNullOrEmpty(service.DockerfilePath))
        {
            // 有 build.dockerfile：照它確定專案（對齊 VS）
            var fromDockerfile = FastDevDetector.FromDockerfile(
                service.WorkingDirectory, service.DockerfilePath, settings.DefaultRuntimeImage);
            if (fromDockerfile != null)
            {
                chosen = fromDockerfile.CsprojRelativePath;
                runtimeImage = fromDockerfile.RuntimeImage;
            }
            else
            {
                // Dockerfile 與 csproj 不同層（monorepo 常見）：專案改由名稱比對確定，
                // 但 runtime image 仍以這個 Dockerfile 的 FROM 為準，退回預設會掛錯 aspnet 版本
                runtimeImage = FastDevDetector.ReadRuntimeImage(service.DockerfilePath);
            }
        }

        // Dockerfile 路線沒結果（無 build 資訊、或 Dockerfile 路徑失效）就退回「資料夾名 = 服務名」完全比對，
        // 找不到才跳過（絕不亂挑別的專案）。不在 Dockerfile 路線失敗時直接放棄：整包服務同時被判非 .NET，
        // 幾乎都是 Dockerfile 路線歪掉，不是真的沒有 .NET 專案
        chosen ??= scannedCsproj.Value.FirstOrDefault(c =>
            c.Split('/')[0].Equals(service.Name, StringComparison.OrdinalIgnoreCase));

        if (chosen == null)
        {
            skipReason = FastDevSkipReason(service);
            return null;
        }

        var projectDir = Path.GetDirectoryName(
            Path.Combine(service.WorkingDirectory, chosen.Replace('/', Path.DirectorySeparatorChar)));
        runtimeImage ??= projectDir == null
            ? null
            : FastDevDetector.ReadRuntimeImage(Path.Combine(projectDir, "Dockerfile"));
        runtimeImage ??= settings.DefaultRuntimeImage;

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

    private static string FastDevSkipReason(DockerService service)
    {
        if (!Directory.Exists(service.WorkingDirectory))
            return "資料夾不存在";
        if (string.IsNullOrEmpty(service.DockerfilePath))
            return "compose 無 build.dockerfile，且找不到同名資料夾的 csproj";
        if (!File.Exists(service.DockerfilePath))
            return $"Dockerfile 不存在（{service.DockerfilePath}），且找不到同名資料夾的 csproj";
        return $"Dockerfile 同目錄無 csproj（{service.DockerfilePath}），且找不到同名資料夾的 csproj";
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
            using var p = ProcessLauncher.Start(psi);
            _dotnetSdkAvailable = (await Task.Run(async () => { await p.WaitForExitAsync(); return p.ExitCode; })) == 0;
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

        ProcessLauncher.StartDetached(new ProcessStartInfo
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
    private void OpenReleasePort()
    {
        var window = new Views.DockerRepairWindow(_dockerCli, portOnly: true)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var settings = await _settingsService.LoadAsync();
        var settingsWindow = new Views.SettingsWindow(settings, _updateService);
        settingsWindow.Owner = Application.Current.MainWindow;

        if (settingsWindow.ShowDialog() == true)
        {
            await _settingsService.SaveAsync(settings);
            ApplyDockerModeSettings(settings);
            ApplySettings(settings);
            await RefreshAvailableEnvironmentsAsync();

            await _monitor.StopAsync();
            _monitor.Start(TimeSpan.FromSeconds(settings.PollIntervalSeconds));
            StatusMessage = "設定已儲存並套用";
        }
    }
}
