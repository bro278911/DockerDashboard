using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerDashboard.Models;
using DockerDashboard.Services;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;

namespace DockerDashboard.ViewModels;

public partial class MainViewModel
{
    // dev server / 一次性指令行程追蹤（key 為專案實例；僅 UI 執行緒讀寫）。
    // 刻意依賴 reference equality（FrontendProject 未覆寫 Equals/GetHashCode）：此物件可變，
    // 若日後改成值相等語意，Name/Group 一改 hash 就變，字典項目會找不回來造成行程洩漏。
    private sealed class FrontendProcess
    {
        public required ProcessStream Stream { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public bool UserStopped { get; set; }
        public Task? ReaderTask { get; set; }
    }

    private readonly Dictionary<FrontendProject, FrontendProcess> _devProcesses = [];
    private readonly Dictionary<FrontendProject, FrontendProcess> _oneShotProcesses = [];

    public ObservableCollection<FrontendProject> InternalProjects { get; } = [];
    public ObservableCollection<FrontendProject> ExternalProjects { get; } = [];
    public FrontendLogBuffer InternalLog { get; } = new();
    public FrontendLogBuffer ExternalLog { get; } = new();

    [ObservableProperty]
    private string _internalRunningLabel = "無執行中專案";

    [ObservableProperty]
    private string _externalRunningLabel = "無執行中專案";

    internal ObservableCollection<FrontendProject> ProjectsOf(FrontendGroup group)
        => group == FrontendGroup.Internal ? InternalProjects : ExternalProjects;

    internal static FrontendProject? FindRunningInGroup(
        IEnumerable<FrontendProject> groupProjects, FrontendProject candidate)
        => groupProjects.FirstOrDefault(p => p.IsDevRunning && !ReferenceEquals(p, candidate));

    internal static string BuildRunningLabel(IEnumerable<FrontendProject> groupProjects)
    {
        var running = groupProjects.FirstOrDefault(p => p.IsDevRunning);
        if (running == null) return "無執行中專案";
        var branch = string.IsNullOrEmpty(running.CurrentBranch) ? "非 git" : running.CurrentBranch;
        return $"{running.Name}（{running.FolderName} / {branch}）";
    }

    private void UpdateFrontendRunningLabels()
    {
        InternalRunningLabel = BuildRunningLabel(InternalProjects);
        ExternalRunningLabel = BuildRunningLabel(ExternalProjects);
    }

    internal void AppendFrontendLog(FrontendProject project, string message)
    {
        var buffer = project.Group == FrontendGroup.Internal ? InternalLog : ExternalLog;
        buffer.Append(message);
    }

    [RelayCommand]
    private void ClearInternalLog() => InternalLog.Clear();

    [RelayCommand]
    private void ClearExternalLog() => ExternalLog.Clear();

    // 兩組 log 面板共用：CommandParameter 綁對應的 InternalLog/ExternalLog，不必各自複製一份指令
    [RelayCommand]
    private async Task ExportFrontendLogAsync(FrontendLogBuffer? log)
    {
        if (log == null) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "匯出日誌",
            Filter = "文字檔 (*.txt)|*.txt|日誌檔 (*.log)|*.log",
            FileName = $"frontend-logs-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (dialog.ShowDialog() != true) return;

        var snapshot = log.Lines.ToArray();
        await System.IO.File.WriteAllLinesAsync(dialog.FileName, snapshot);
        StatusMessage = $"日誌已匯出到 {dialog.FileName}";
    }

    [RelayCommand]
    private void CopySelectedFrontendLog(System.Collections.IList? selectedItems)
    {
        if (selectedItems is null || selectedItems.Count == 0) return;
        var text = string.Join(Environment.NewLine, selectedItems.Cast<string>());
        System.Windows.Clipboard.SetText(text);
        StatusMessage = $"已複製 {selectedItems.Count} 行日誌";
    }

    [RelayCommand]
    private void CopyAllFrontendLog(FrontendLogBuffer? log)
    {
        if (log == null || log.Lines.Count == 0) return;
        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, log.Lines));
        StatusMessage = $"已複製全部 {log.Lines.Count} 行日誌";
    }

    [RelayCommand]
    private async Task StartFrontendAsync(FrontendProject? project)
    {
        if (project == null || _devProcesses.ContainsKey(project)) return;

        // 同組互斥：已有執行中專案時先確認再關舊起新
        var running = FindRunningInGroup(ProjectsOf(project.Group), project);
        if (running != null)
        {
            var confirm = System.Windows.MessageBox.Show(
                $"同組已有「{running.Name}」（{running.FolderName} / {running.CurrentBranch}）執行中。\n\n" +
                $"要停止它並啟動「{project.Name}」嗎？",
                "同組互斥確認",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;
            await StopFrontendAsync(running);
        }

        // 啟動前重新讀取分支，確保顯示與實際一致；讀取失敗（git 不在 PATH 等）不阻擋啟動
        if (_gitService.IsGitRepository(project.FolderPath))
        {
            try
            {
                project.CurrentBranch = await _gitService.GetCurrentBranchAsync(project.FolderPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Frontend] 讀取 {project.Name} 分支失敗: {ex.Message}");
            }
        }

        ProcessStream stream;
        try
        {
            stream = _nodeService.Start(project.FolderPath, project.DevCommand);
        }
        catch (Exception ex)
        {
            project.Status = FrontendStatus.Crashed;
            AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ❌ [{project.Name}] 啟動失敗: {ex.Message}");
            StatusMessage = $"❌ {project.Name} 啟動失敗";
            return;
        }

        // 上面的互斥確認、讀分支都要 await，期間專案可能已被 RemoveFrontendProjectCommand 移除
        // （該 command 未被本 command 的並行限制擋住）；此時立刻收掉剛起的孤兒行程，不註冊追蹤
        if (!ProjectsOf(project.Group).Contains(project))
        {
            stream.Dispose();
            return;
        }

        var ctx = new FrontendProcess { Stream = stream, Cts = new CancellationTokenSource() };
        _devProcesses.Add(project, ctx); // Add 而非索引賦值：重複註冊時直接炸出來，而非靜默覆寫遺失舊 ctx
        project.IsDevRunning = true;
        project.Status = FrontendStatus.Running;
        AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ▶ [{project.Name}] 啟動 dev server（{project.DevCommand}）於 {project.FolderPath}");
        StatusMessage = $"▶ {project.Name} dev server 啟動中（{project.CurrentBranch}）";
        UpdateFrontendRunningLabels();

        ctx.ReaderTask = Task.Run(() => RunDevProcessAsync(project, ctx));
    }

    // 外層兜底：確保這個 Task 一定會完成（不 fault），StopFrontendAsync 等它時才不會被
    // 非預期例外打斷（例如 Dispatcher 關閉期間的 TaskCanceledException）
    private async Task RunDevProcessAsync(FrontendProject project, FrontendProcess ctx)
    {
        try
        {
            await RunDevProcessCoreAsync(project, ctx);
        }
        catch (Exception ex)
        {
            AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ⚠️ [{project.Name}] dev server 監控發生非預期例外: {ex.Message}");
        }
    }

    private async Task RunDevProcessCoreAsync(FrontendProject project, FrontendProcess ctx)
    {
        try
        {
            await Task.WhenAll(
                ReadFrontendStreamAsync(project, ctx.Stream.StandardOutput, ctx.Cts.Token),
                ReadFrontendStreamAsync(project, ctx.Stream.StandardError, ctx.Cts.Token));
        }
        catch (OperationCanceledException) { }

        int exitCode;
        try
        {
            await ctx.Stream.WaitForExitAsync(CancellationToken.None);
            exitCode = ctx.Stream.ExitCode;
        }
        catch
        {
            exitCode = -1;
        }

        await (Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _devProcesses.Remove(project);
            project.IsDevRunning = false;

            if (ctx.UserStopped)
            {
                project.Status = FrontendStatus.Stopped;
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ⏹ [{project.Name}] dev server 已停止");
            }
            else
            {
                project.Status = FrontendStatus.Crashed;
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ⚠️ [{project.Name}] dev server 意外結束（exit code {exitCode}）");
                try
                {
                    _notifyIcon?.ShowBalloonTip(
                        3000,
                        "前端 dev server 異常結束",
                        $"{project.Name}（{project.FolderName}）已停止（exit code {exitCode}）",
                        Forms.ToolTipIcon.Warning);
                }
                catch (ObjectDisposedException) { }
            }

            UpdateFrontendRunningLabels();
            ctx.Stream.Dispose();
            ctx.Cts.Dispose();
        }).Task ?? Task.CompletedTask);
    }

    private async Task ReadFrontendStreamAsync(
        FrontendProject project, System.IO.StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            AppendFrontendLog(project, $"[{project.Name}] {line}");
        }
    }

    [RelayCommand]
    private async Task StopFrontendAsync(FrontendProject? project)
    {
        if (project == null || !_devProcesses.TryGetValue(project, out var ctx)) return;

        ctx.UserStopped = true;
        ctx.Cts.Cancel();
        ctx.Stream.Kill(); // entireProcessTree: true，整樹殺掉 node 子行程
        if (ctx.ReaderTask != null)
        {
            // 等舊行程完全結束再返回，互斥切換時避免 port 尚未釋放；
            // 加 timeout 避免 kill 失敗（權限、殭屍子行程）時卡死整個前端啟停功能
            try
            {
                await ctx.ReaderTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ⚠️ [{project.Name}] 停止逾時或發生例外: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private async Task AddFrontendProjectAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "選擇前端專案資料夾" };
        if (dialog.ShowDialog() != true) return;

        var folder = dialog.FolderName;
        if (InternalProjects.Concat(ExternalProjects)
            .Any(p => p.FolderPath.Equals(folder, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "⚠ 此資料夾已加入過";
            return;
        }

        // 無 package.json 仍可硬加（指令可自訂），但先提示確認
        if (!System.IO.File.Exists(System.IO.Path.Combine(folder, "package.json")))
        {
            var confirm = System.Windows.MessageBox.Show(
                $"{folder}\n\n找不到 package.json，仍要加入嗎？",
                "加入前端專案",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;
        }

        var project = new FrontendProject
        {
            Name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')),
            FolderPath = folder,
        };
        if (_gitService.IsGitRepository(folder))
        {
            try
            {
                project.CurrentBranch = await _gitService.GetCurrentBranchAsync(folder);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Frontend] 讀取 {folder} 分支失敗: {ex.Message}");
            }
        }

        var editor = new Views.FrontendProjectDialog(project)
        {
            Owner = Application.Current.MainWindow
        };
        if (editor.ShowDialog() != true) return;

        ProjectsOf(project.Group).Add(project);
        await SaveSettingsAsync();
        StatusMessage = $"✅ 已加入前端專案 {project.Name}";
    }

    [RelayCommand]
    private async Task RemoveFrontendProjectAsync(FrontendProject? project)
    {
        if (project == null) return;

        // 執行中先停止再移除（各自的 TryGetValue 守衛已處理「沒在跑」情況，不必外層再查表）
        await CancelOneShotAsync(project);
        await StopFrontendAsync(project);

        ProjectsOf(project.Group).Remove(project);
        await SaveSettingsAsync();
        StatusMessage = $"已移除前端專案 {project.Name}";
    }

    // 啟動載入：還原設定中的前端專案並背景更新分支。
    // 刻意不比照後端用 Where(Directory.Exists) 過濾：資料夾被刪/改名時保留使用者設定的自訂
    // 指令較有意義，按啟動才以錯誤訊息失敗即可，不強制連專案清單一起消失。
    internal void LoadFrontendProjects(AppSettings settings)
    {
        foreach (var config in settings.FrontendProjects)
        {
            var project = FrontendProject.FromConfig(config);
            ProjectsOf(project.Group).Add(project);
        }
        _ = RefreshFrontendBranchesAsync();
    }

    private async Task RefreshFrontendBranchesAsync()
    {
        foreach (var project in InternalProjects.Concat(ExternalProjects).ToList())
        {
            if (!_gitService.IsGitRepository(project.FolderPath)) continue;
            try
            {
                var branch = await _gitService.GetCurrentBranchAsync(project.FolderPath);
                await (Application.Current?.Dispatcher.InvokeAsync(() => project.CurrentBranch = branch).Task
                       ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                // 單一 repo 讀取失敗（git 不在 PATH 等）不中止其餘專案的分支更新
                System.Diagnostics.Debug.WriteLine($"[Frontend] 讀取 {project.Name} 分支失敗: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private Task RunInstallAsync(FrontendProject? project)
        => RunOneShotAsync(project, project?.InstallCommand, "install");

    [RelayCommand]
    private Task RunTestAsync(FrontendProject? project)
        => RunOneShotAsync(project, project?.TestCommand, "vitest");

    [RelayCommand]
    private Task RunE2eAsync(FrontendProject? project)
        => RunOneShotAsync(project, project?.E2eCommand, "e2e");

    // 同專案一次只跑一個一次性指令；dev server 執行中仍可跑。全程無真正的 await，非 async。
    private Task RunOneShotAsync(FrontendProject? project, string? command, string label)
    {
        if (project == null || string.IsNullOrWhiteSpace(command)
            || _oneShotProcesses.ContainsKey(project)) return Task.CompletedTask;

        ProcessStream stream;
        try
        {
            stream = _nodeService.Start(project.FolderPath, command);
        }
        catch (Exception ex)
        {
            AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ❌ [{project.Name}] {label} 啟動失敗: {ex.Message}");
            StatusMessage = $"❌ {project.Name} {label} 啟動失敗";
            return Task.CompletedTask;
        }

        var ctx = new FrontendProcess { Stream = stream, Cts = new CancellationTokenSource() };
        _oneShotProcesses[project] = ctx;
        project.IsOneShotRunning = true;
        AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ▶ [{project.Name}] {label}（{command}）");
        StatusMessage = $"▶ {project.Name} 執行 {label} 中...";

        ctx.ReaderTask = Task.Run(() => RunOneShotProcessAsync(project, ctx, label));
        return Task.CompletedTask;
    }

    // 外層兜底：確保這個 Task 一定會完成（不 fault），CancelOneShotAsync 等它時才不會被
    // 非預期例外打斷
    private async Task RunOneShotProcessAsync(FrontendProject project, FrontendProcess ctx, string label)
    {
        try
        {
            await RunOneShotProcessCoreAsync(project, ctx, label);
        }
        catch (Exception ex)
        {
            AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ⚠️ [{project.Name}] {label} 監控發生非預期例外: {ex.Message}");
        }
    }

    private async Task RunOneShotProcessCoreAsync(FrontendProject project, FrontendProcess ctx, string label)
    {
        try
        {
            await Task.WhenAll(
                ReadFrontendStreamAsync(project, ctx.Stream.StandardOutput, ctx.Cts.Token),
                ReadFrontendStreamAsync(project, ctx.Stream.StandardError, ctx.Cts.Token));
        }
        catch (OperationCanceledException) { }

        int exitCode;
        try
        {
            await ctx.Stream.WaitForExitAsync(CancellationToken.None);
            exitCode = ctx.Stream.ExitCode;
        }
        catch
        {
            exitCode = -1;
        }

        await (Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _oneShotProcesses.Remove(project);
            project.IsOneShotRunning = false; // 不動 Status：避免蓋掉 dev server 的 Crashed/Running

            if (ctx.UserStopped)
            {
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ⏹ [{project.Name}] {label} 已取消");
                StatusMessage = $"⏹ {project.Name} {label} 已取消";
            }
            else
            {
                var icon = exitCode == 0 ? "✅" : "❌";
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] {icon} [{project.Name}] {label} 結束（exit code {exitCode}）");
                StatusMessage = $"{icon} {project.Name} {label} 結束（exit code {exitCode}）";
            }

            ctx.Stream.Dispose();
            ctx.Cts.Dispose();
        }).Task ?? Task.CompletedTask);
    }

    [RelayCommand]
    private async Task CancelOneShotAsync(FrontendProject? project)
    {
        if (project == null || !_oneShotProcesses.TryGetValue(project, out var ctx)) return;

        ctx.UserStopped = true;
        ctx.Cts.Cancel();
        ctx.Stream.Kill(); // 整樹終止，收尾與狀態回復由 reader 收斂處理
        if (ctx.ReaderTask != null)
        {
            try
            {
                await ctx.ReaderTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ⚠️ [{project.Name}] 取消逾時或發生例外: {ex.Message}");
            }
        }
    }

    /// <summary>App 關閉時整樹終止所有前端行程（Dispose 呼叫）</summary>
    internal void StopAllFrontendProcesses()
    {
        var all = _devProcesses.Values.Concat(_oneShotProcesses.Values).ToArray();

        // 只 kill 不 Dispose：reader 仍在讀取 stream，這時 Dispose 會讓它讀取中拋例外
        foreach (var ctx in all)
        {
            ctx.UserStopped = true;
            ctx.Cts.Cancel();
            ctx.Stream.Kill(); // entireProcessTree: true
        }

        // Dispose() 為同步方法，用 WaitAll 給行程真正退出、reader 收斂的機會（設短逾時避免卡死關閉流程）
        var readerTasks = all.Select(ctx => ctx.ReaderTask).Where(t => t != null).Select(t => t!).ToArray();
        if (readerTasks.Length > 0)
            Task.WaitAll(readerTasks, TimeSpan.FromSeconds(3));

        // 逐一 Dispose：reader 若已跑完自己的收尾會是重複 Dispose，安全（ProcessStream 有 _disposed 防護，
        // CancellationTokenSource.Dispose 本身也可重複呼叫）；reader 沒機會跑完時則確保資源真的釋放
        foreach (var ctx in all)
        {
            ctx.Stream.Dispose();
            ctx.Cts.Dispose();
        }

        _devProcesses.Clear();
        _oneShotProcesses.Clear();
    }
}
