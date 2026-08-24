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
        if (project == null || _devProcesses.ContainsKey(project) || project.IsStarting) return;

        project.IsStarting = true;
        try
        {
            await StartFrontendCoreAsync(project);
        }
        finally
        {
            project.IsStarting = false;
        }
    }

    private async Task StartFrontendCoreAsync(FrontendProject project)
    {
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

            // 停不掉就不要硬起：舊行程還佔著同一個 port，硬起只會讓新的 vite 以 EADDRINUSE 死掉，
            // 兩個專案還會同時被標成執行中
            if (!await StopFrontendAsync(running))
            {
                StatusMessage = $"⚠ 無法停止 {running.Name}，已中止啟動 {project.Name}";
                return;
            }
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
            stream.Kill(); // 先終止行程樹，再釋放 handle，否則會留下佔著 port 的孤兒 node
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

    // 監控 dev server 直到結束。收尾放在 finally：任何非預期例外（pipe IOException、
    // dispatcher 關閉期的 TaskCanceledException 等）都不能讓專案卡在「執行中」而再也起不動
    private async Task RunDevProcessAsync(FrontendProject project, FrontendProcess ctx)
    {
        var exitCode = -1;
        try
        {
            try
            {
                await Task.WhenAll(
                    ReadFrontendStreamAsync(project, ctx.Stream.StandardOutput, ctx.Cts.Token),
                    ReadFrontendStreamAsync(project, ctx.Stream.StandardError, ctx.Cts.Token));
            }
            catch (OperationCanceledException) { }

            try
            {
                // 有界等待：Kill 因權限或殭屍子行程失敗時，無限等待會讓 finally 永遠跑不到，
                // 專案就永久卡在「執行中」再也起不動
                await ctx.Stream.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(15));
                exitCode = ctx.Stream.ExitCode;
            }
            catch
            {
                exitCode = -1;
            }
        }
        catch (Exception ex)
        {
            AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ⚠️ [{project.Name}] dev server 監控發生非預期例外: {ex.Message}");
        }
        finally
        {
            await FinishDevProcessAsync(project, ctx, exitCode);
        }
    }

    private Task FinishDevProcessAsync(FrontendProject project, FrontendProcess ctx, int exitCode)
    {
        void Finish()
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
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            // App 已關閉，dispatcher 不在了：至少確保行程與資源被釋放
            ctx.Stream.Dispose();
            ctx.Cts.Dispose();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(Finish).Task;
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

    /// <returns>行程確實已結束為 true；逾時或發生例外為 false（呼叫端據此決定要不要續行）</returns>
    [RelayCommand]
    private async Task<bool> StopFrontendAsync(FrontendProject? project)
    {
        if (project == null || !_devProcesses.TryGetValue(project, out var ctx)) return true;

        ctx.UserStopped = true;
        ctx.Cts.Cancel();
        ctx.Stream.Kill(); // entireProcessTree: true，整樹殺掉 node 子行程
        if (ctx.ReaderTask == null) return true;

        // 等舊行程完全結束再返回，互斥切換時避免 port 尚未釋放；
        // 加 timeout 避免 kill 失敗（權限、殭屍子行程）時卡死整個前端啟停功能
        try
        {
            await ctx.ReaderTask.WaitAsync(TimeSpan.FromSeconds(10));
            return true;
        }
        catch (Exception ex)
        {
            AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ⚠️ [{project.Name}] 停止逾時或發生例外: {ex.Message}");
            return false;
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
    private async Task EditFrontendProjectAsync(FrontendProject? project)
    {
        if (project == null) return;

        // 執行中不可編輯：組別決定行程追蹤、log 歸屬與同組互斥，跑到一半改掉會全部錯亂
        if (!project.IsIdle)
        {
            StatusMessage = $"⚠ {project.Name} 執行中，請先停止再編輯";
            return;
        }

        // 傳副本進 dialog：dialog 直接雙向綁定 model，若傳本尊，使用者按「取消」時
        // 改動早已寫進物件。確認後才由 ApplyFrontendEdit 套回本尊
        var draft = FrontendProject.FromConfig(project.ToConfig());

        var editor = new Views.FrontendProjectDialog(draft, isEdit: true)
        {
            Owner = Application.Current.MainWindow
        };
        if (editor.ShowDialog() != true) return;

        // 再驗一次：modal dialog 的巢狀訊息迴圈會讓 UI 執行緒繼續處理其他工作，
        // 開啟期間專案可能已被啟動（啟動流程的 await continuation 在這裡完成）
        if (!project.IsIdle)
        {
            StatusMessage = $"⚠ {project.Name} 已在執行中，編輯未套用";
            return;
        }

        ApplyFrontendEdit(project, draft.ToConfig(), InternalProjects, ExternalProjects);
        await SaveSettingsAsync();
        StatusMessage = $"✅ 已更新前端專案 {project.Name}";
    }

    /// <summary>套用編輯結果：更新可編輯欄位，組別有變時在兩個集合間搬移（資料夾路徑不可編輯）</summary>
    internal static void ApplyFrontendEdit(
        FrontendProject project,
        FrontendProjectConfig edited,
        ObservableCollection<FrontendProject> internalProjects,
        ObservableCollection<FrontendProject> externalProjects)
    {
        project.Name = edited.Name;
        project.DevCommand = edited.DevCommand;
        project.InstallCommand = edited.InstallCommand;
        project.TestCommand = edited.TestCommand;
        project.E2eCommand = edited.E2eCommand;

        var oldGroup = project.Group;
        if (edited.Group == oldGroup) return;

        project.Group = edited.Group;
        var from = oldGroup == FrontendGroup.Internal ? internalProjects : externalProjects;
        var to = edited.Group == FrontendGroup.Internal ? internalProjects : externalProjects;
        from.Remove(project);
        to.Add(project);
    }

    [RelayCommand]
    private async Task RemoveFrontendProjectAsync(FrontendProject? project)
    {
        if (project == null) return;

        // 執行中先停止再移除（各自的 TryGetValue 守衛已處理「沒在跑」情況，不必外層再查表）。
        // 任一沒確認結束就不移除：否則行程還活著卻從 UI 與設定消失，使用者再也沒有停止它的入口
        var oneShotStopped = await CancelOneShotAsync(project);
        var devStopped = await StopFrontendAsync(project);
        if (!oneShotStopped || !devStopped)
        {
            StatusMessage = $"⚠ {project.Name} 的行程未確認結束，已保留專案（可再次嘗試停止）";
            return;
        }

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

    // 監控一次性指令直到結束。收尾同樣放 finally：例外逃出時 IsOneShotRunning 若沒還原，
    // install/vitest/e2e 三顆鈕會永遠 disable
    private async Task RunOneShotProcessAsync(FrontendProject project, FrontendProcess ctx, string label)
    {
        var exitCode = -1;
        try
        {
            try
            {
                await Task.WhenAll(
                    ReadFrontendStreamAsync(project, ctx.Stream.StandardOutput, ctx.Cts.Token),
                    ReadFrontendStreamAsync(project, ctx.Stream.StandardError, ctx.Cts.Token));
            }
            catch (OperationCanceledException) { }

            try
            {
                // 有界等待：Kill 因權限或殭屍子行程失敗時，無限等待會讓 finally 永遠跑不到，
                // 專案就永久卡在「執行中」再也起不動
                await ctx.Stream.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(15));
                exitCode = ctx.Stream.ExitCode;
            }
            catch
            {
                exitCode = -1;
            }
        }
        catch (Exception ex)
        {
            AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ⚠️ [{project.Name}] {label} 監控發生非預期例外: {ex.Message}");
        }
        finally
        {
            await FinishOneShotProcessAsync(project, ctx, label, exitCode);
        }
    }

    private Task FinishOneShotProcessAsync(
        FrontendProject project, FrontendProcess ctx, string label, int exitCode)
    {
        void Finish()
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
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            ctx.Stream.Dispose();
            ctx.Cts.Dispose();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(Finish).Task;
    }

    /// <returns>行程確實已結束為 true；逾時或發生例外為 false</returns>
    [RelayCommand]
    private async Task<bool> CancelOneShotAsync(FrontendProject? project)
    {
        if (project == null || !_oneShotProcesses.TryGetValue(project, out var ctx)) return true;

        ctx.UserStopped = true;
        ctx.Cts.Cancel();
        ctx.Stream.Kill(); // 整樹終止，收尾與狀態回復由 reader 收斂處理
        if (ctx.ReaderTask == null) return true;

        try
        {
            await ctx.ReaderTask.WaitAsync(TimeSpan.FromSeconds(10));
            return true;
        }
        catch (Exception ex)
        {
            AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ⚠️ [{project.Name}] 取消逾時或發生例外: {ex.Message}");
            return false;
        }
    }

    /// <summary>App 關閉時整樹終止所有前端行程（Dispose 呼叫，執行於 UI 執行緒）</summary>
    internal void StopAllFrontendProcesses()
    {
        var all = _devProcesses.Values.Concat(_oneShotProcesses.Values).ToArray();

        // 不等 reader 收斂：本方法由 MainWindow.OnClosed → Dispose 在 UI 執行緒呼叫，而每個
        // reader 收尾都要 Dispatcher.InvokeAsync 回同一條執行緒，在此阻塞等待會自己卡死自己。
        // Kill 送出終止請求、Dispose 釋放 handle，兩者皆同步完成，行程樹交給 OS 回收。
        foreach (var ctx in all)
        {
            ctx.UserStopped = true;
            ctx.Cts.Cancel();
            ctx.Stream.Dispose(); // 內含 Kill(entireProcessTree: true)
            ctx.Cts.Dispose();
        }

        _devProcesses.Clear();
        _oneShotProcesses.Clear();
    }
}
