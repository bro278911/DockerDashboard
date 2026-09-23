using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    private readonly Dictionary<FrontendProject, int> _devStartAttempts = [];
    public ObservableCollection<FrontendProject> InternalProjects { get; } = [];
    public ObservableCollection<FrontendProject> ExternalProjects { get; } = [];
    public LogBuffer InternalLog { get; } = new();
    public LogBuffer ExternalLog { get; } = new();

    [ObservableProperty]
    private string _internalRunningLabel = "無執行中專案";

    [ObservableProperty]
    private string _externalRunningLabel = "無執行中專案";

    internal ObservableCollection<FrontendProject> ProjectsOf(FrontendGroup group)
        => group == FrontendGroup.Internal ? InternalProjects : ExternalProjects;

    internal static string BuildRunningLabel(IEnumerable<FrontendProject> groupProjects)
    {
        var running = groupProjects.FirstOrDefault(p => p.IsDevRunning);
        if (running == null) return "無執行中專案";
        return $"{running.Name}（{running.FolderName} / {running.BranchDisplay}）";
    }

    private void UpdateFrontendRunningLabels()
    {
        InternalRunningLabel = BuildRunningLabel(InternalProjects);
        ExternalRunningLabel = BuildRunningLabel(ExternalProjects);
    }

    internal void AppendFrontendLog(FrontendProject project, string message)
    {
        var buffer = project.Group == FrontendGroup.Internal ? InternalLog : ExternalLog;
        buffer.Append($"[{project.Name}] {message}");
    }

    [RelayCommand]
    private void ClearInternalLog() => InternalLog.Clear();

    [RelayCommand]
    private void ClearExternalLog() => ExternalLog.Clear();

    // 兩組 log 面板共用：CommandParameter 綁對應的 InternalLog/ExternalLog，不必各自複製一份指令
    [RelayCommand]
    private async Task ExportFrontendLogAsync(LogBuffer? log)
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
    private void CopyAllFrontendLog(LogBuffer? log)
    {
        if (log == null || log.Lines.Count == 0) return;
        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, log.Lines));
        StatusMessage = $"已複製全部 {log.Lines.Count} 行日誌";
    }

    // manager 在背景執行緒觸發此事件；LogBuffer.Append 內部已用 ConcurrentQueue +
    // Dispatcher 排程收斂寫入，故此處不必再包一層 InvokeAsync
    private void OnFrontendOutput(object? sender, FrontendOutputEventArgs e)
        => AppendFrontendLog(e.Project, e.Line);

    private void OnFrontendStateChanged(object? sender, FrontendStateEventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (e.Kind == FrontendProcessKind.Dev)
            {
                e.Project.IsDevRunning = e.State == FrontendProcessState.Running;
                e.Project.Status = e.State switch
                {
                    FrontendProcessState.Running => FrontendStatus.Running,
                    FrontendProcessState.Crashed => FrontendStatus.Crashed,
                    _ => FrontendStatus.Stopped
                };

                if (e.State == FrontendProcessState.Crashed)
                {
                    AppendFrontendLog(e.Project,
                        $"[{DateTime.Now:HH:mm:ss}] ⚠️ dev server 意外結束（exit code {e.ExitCode}）");
                    try
                    {
                        _notifyIcon?.ShowBalloonTip(
                            3000,
                            "前端 dev server 異常結束",
                            $"{e.Project.Name}（{e.Project.FolderName}）已停止（exit code {e.ExitCode}）",
                            Forms.ToolTipIcon.Warning);
                    }
                    catch (ObjectDisposedException) { }
                }
                else if (e.State == FrontendProcessState.Stopped)
                {
                    AppendFrontendLog(e.Project, $"[{DateTime.Now:HH:mm:ss}] ⏹ dev server 已停止");
                }

                UpdateFrontendRunningLabels();
            }
            else
            {
                e.Project.IsOneShotRunning = e.State == FrontendProcessState.Running;
                if (e.State != FrontendProcessState.Running)
                {
                    // 一次性指令一律回報 Stopped，靠 UserStopped 區分「使用者取消」與「自然結束」，
                    // 否則使用者取消會被誤顯示成依 exit code 判定的失敗訊息
                    if (e.UserStopped)
                    {
                        AppendFrontendLog(e.Project, $"[{DateTime.Now:HH:mm:ss}] ⏹ {e.Label} 已取消");
                        StatusMessage = $"⏹ {e.Project.Name} {e.Label} 已取消";
                    }
                    else
                    {
                        var icon = e.ExitCode == 0 ? "✅" : "❌";
                        AppendFrontendLog(e.Project,
                            $"[{DateTime.Now:HH:mm:ss}] {icon} {e.Label} 結束（exit code {e.ExitCode}）");
                        StatusMessage = $"{icon} {e.Project.Name} {e.Label} 結束（exit code {e.ExitCode}）";
                    }
                }
            }
        });
    }

    [RelayCommand]
    private async Task StartFrontendAsync(FrontendProject? project)
    {
        if (project == null) return;

        var attempt = BeginDevStartAttempt(project);
        project.IsStarting = true;
        try
        {
            var result = await _frontendProcesses.StartDevAsync(project, ProjectsOf(project.Group));

            if (result is StartResult.GroupOccupied occupied)
            {
                var confirm = System.Windows.MessageBox.Show(
                    $"同組已有「{occupied.Occupant.Name}」（{occupied.Occupant.FolderName} / {occupied.Occupant.BranchDisplay}）執行中。\n\n" +
                    $"要停止它並啟動「{project.Name}」嗎？",
                    "同組互斥確認",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);
                if (confirm != System.Windows.MessageBoxResult.Yes) return;

                if (!await _frontendProcesses.StopDevAsync(occupied.Occupant))
                {
                    StatusMessage = $"⚠ 無法停止 {occupied.Occupant.Name}，已中止啟動 {project.Name}";
                    return;
                }

                result = await _frontendProcesses.StartDevAsync(project, ProjectsOf(project.Group));
            }

            switch (result)
            {
                case StartResult.Started:
                    await RefreshBranchAsync(project);
                    if (!IsDevStartAttemptActive(project, attempt))
                        return;
                    AppendFrontendLog(project,
                        $"[{DateTime.Now:HH:mm:ss}] ▶ 啟動 dev server（{project.DevCommand}）於 {project.FolderPath}");
                    StatusMessage = $"▶ {project.Name} dev server 啟動中（{project.BranchDisplay}）";
                    UpdateFrontendRunningLabels();
                    break;
                case StartResult.Failed failed:
                    project.Status = FrontendStatus.Crashed;
                    AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ❌ 啟動失敗: {failed.Message}");
                    StatusMessage = $"❌ {project.Name} 啟動失敗";
                    break;
            }
        }
        finally
        {
            if (IsCurrentDevStartAttempt(project, attempt))
                project.IsStarting = false;
        }
    }

    [RelayCommand]
    private async Task StopFrontendAsync(FrontendProject? project)
    {
        if (project == null) return;
        var stopAttempt = InvalidateDevStartAttempt(project);
        if (!await _frontendProcesses.StopDevAsync(project))
        {
            StatusMessage = $"⚠ {project.Name} 停止未完成，行程可能仍在執行";
            return;
        }

        if (IsCurrentDevStartAttempt(project, stopAttempt))
            project.IsStarting = false;
    }

    [RelayCommand]
    private Task RunInstallAsync(FrontendProject? project) => RunOneShotAsync(project, project?.InstallCommand, "install");

    [RelayCommand]
    private Task RunTestAsync(FrontendProject? project) => RunOneShotAsync(project, project?.TestCommand, "vitest");

    [RelayCommand]
    private Task RunE2eAsync(FrontendProject? project) => RunOneShotAsync(project, project?.E2eCommand, "e2e");

    private async Task RunOneShotAsync(FrontendProject? project, string? command, string label)
    {
        if (project == null || string.IsNullOrWhiteSpace(command)) return;

        var result = await _frontendProcesses.RunOneShotAsync(project, command, label);
        switch (result)
        {
            case StartResult.Started:
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ▶ {label}（{command}）");
                StatusMessage = $"▶ {project.Name} 執行 {label} 中...";
                break;
            case StartResult.Failed failed:
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ❌ {label} 啟動失敗: {failed.Message}");
                StatusMessage = $"❌ {project.Name} {label} 啟動失敗";
                break;
        }
    }

    [RelayCommand]
    private async Task CancelOneShotAsync(FrontendProject? project)
    {
        if (project == null) return;
        if (!await _frontendProcesses.CancelOneShotAsync(project))
            StatusMessage = $"⚠ {project.Name} 取消未完成，行程可能仍在執行";
    }

    private async Task RefreshBranchAsync(FrontendProject project)
    {
        project.IsGitRepo = _gitService.IsGitRepository(project.FolderPath);
        if (!project.IsGitRepo) return;
        try
        {
            project.CurrentBranch = await _gitService.GetCurrentBranchAsync(project.FolderPath);
            project.IsDirty = await _gitService.IsDirtyAsync(project.FolderPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Frontend] 讀取 {project.Name} 分支失敗: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SwitchFrontendBranchAsync(FrontendProject? project)
    {
        if (project == null || !project.IsGitRepo) return;
        // npm install 等一次性指令或啟動途中也擋，避免 git 改檔與指令同時動工作區
        if (project.IsGitBusy || project.IsOneShotRunning || project.IsStarting)
        {
            StatusMessage = "⚠ 已有操作進行中，請稍候";
            return;
        }

        if (project.IsDevRunning)
        {
            var confirmRunning = System.Windows.MessageBox.Show(
                $"專案 {project.Name} 的 dev server 執行中，切換分支可能造成不一致。\n\n確定要繼續嗎？",
                "Dev Server 執行中警告",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirmRunning != System.Windows.MessageBoxResult.Yes) return;
        }

        if (project.IsDirty)
        {
            var confirmDirty = System.Windows.MessageBox.Show(
                $"專案 {project.Name} 有未提交的變更，切換分支可能導致衝突。\n\n確定要繼續嗎？",
                "未提交變更警告",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirmDirty != System.Windows.MessageBoxResult.Yes) return;
        }

        project.IsGitBusy = true;
        try
        {
            var localBranches = await _gitService.GetLocalBranchesAsync(project.FolderPath);
            var remoteBranches = await _gitService.GetRemoteBranchesAsync(project.FolderPath);

            var selector = new Views.BranchSelectorWindow(
                project.Name, project.CurrentBranch, localBranches, remoteBranches);
            selector.Owner = Application.Current.MainWindow;

            if (selector.ShowDialog() != true || string.IsNullOrEmpty(selector.SelectedBranch))
                return;

            var targetBranch = selector.SelectedBranch;
            StatusMessage = $"正在切換 {project.Name} 到分支 {targetBranch}...";
            AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] 🔀 切換 → {targetBranch}");

            var (success, output) = await _gitService.CheckoutAsync(project.FolderPath, targetBranch);

            if (success)
            {
                project.CurrentBranch = await _gitService.GetCurrentBranchAsync(project.FolderPath);
                project.IsDirty = await _gitService.IsDirtyAsync(project.FolderPath);
                StatusMessage = $"✅ {project.Name} 已切換到 {project.CurrentBranch}";
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ✅ 分支切換成功: {project.CurrentBranch}");
            }
            else
            {
                StatusMessage = $"⚠ {project.Name} 分支切換失敗";
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ❌ 分支切換失敗: {output}");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ {project.Name} 分支切換失敗";
            AppendFrontendLog(project, $"[例外] {ex.Message}");
        }
        finally
        {
            project.IsGitBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshFrontendGitStatusAsync(FrontendProject? project)
    {
        if (project == null) return;

        project.IsGitRepo = _gitService.IsGitRepository(project.FolderPath);
        if (!project.IsGitRepo) return;

        project.CurrentBranch = await _gitService.GetCurrentBranchAsync(project.FolderPath);
        project.IsDirty = await _gitService.IsDirtyAsync(project.FolderPath);
    }

    [RelayCommand]
    private async Task PullFrontendAsync(FrontendProject? project)
    {
        if (project == null || !project.IsGitRepo) return;
        // npm install 等一次性指令或啟動途中也擋，避免 git 改檔與指令同時動工作區
        if (project.IsGitBusy || project.IsOneShotRunning || project.IsStarting)
        {
            StatusMessage = "⚠ 已有操作進行中，請稍候";
            return;
        }

        if (project.IsDevRunning)
        {
            var confirmRunning = System.Windows.MessageBox.Show(
                $"專案 {project.Name} 的 dev server 執行中，Pull 可能造成不一致。\n\n確定要繼續嗎？",
                "Dev Server 執行中警告",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirmRunning != System.Windows.MessageBoxResult.Yes) return;
        }

        project.IsGitBusy = true;
        try
        {
            var remoteBranches = await _gitService.GetRemoteBranchesAsync(project.FolderPath);

            var selector = new Views.BranchSelectorWindow(
                project.Name, project.CurrentBranch, [], remoteBranches, isPullMode: true);
            selector.Owner = Application.Current.MainWindow;

            if (selector.ShowDialog() != true || string.IsNullOrEmpty(selector.SelectedBranch))
                return;

            var remoteBranch = selector.SelectedBranch;
            StatusMessage = $"正在 pull {project.Name}（{remoteBranch}）...";
            AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ⬇ Pull ← {remoteBranch}");

            var result = await _gitService.PullAsync(project.FolderPath, remoteBranch);

            if (result.BlockedByDirty)
            {
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ⚠ 有未提交變更，請先 commit 或 stash");
                StatusMessage = $"⚠ {project.Name} 有未提交變更，請先 commit 或 stash";
                System.Windows.MessageBox.Show(
                    $"專案 {project.Name} 有未提交的變更，請先 commit 或 stash 後再 Pull。",
                    "有未提交變更",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.Output))
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] {result.Output.Trim()}");

            project.CurrentBranch = await _gitService.GetCurrentBranchAsync(project.FolderPath);
            project.IsDirty = await _gitService.IsDirtyAsync(project.FolderPath);

            if (result.Success)
            {
                StatusMessage = $"✅ {project.Name} pull 成功";
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ✔ Pull 成功");
            }
            else if (result.Restored)
            {
                var shortSha = result.PreviousSha is { Length: >= 7 } sha ? sha[..7] : result.PreviousSha;
                StatusMessage = $"❌ {project.Name} pull 失敗，已還原至 {shortSha}";
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ✖ Pull 失敗，已還原至 {shortSha}");
            }
            else
            {
                StatusMessage = $"❌ {project.Name} pull 失敗，還原失敗: {result.RestoreError}";
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ✖ Pull 失敗，還原失敗: {result.RestoreError}");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ {project.Name} pull 失敗";
            AppendFrontendLog(project, $"[例外] {ex.Message}");
        }
        finally
        {
            project.IsGitBusy = false;
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
        project.IsGitRepo = _gitService.IsGitRepository(folder);
        if (project.IsGitRepo)
        {
            try
            {
                project.CurrentBranch = await _gitService.GetCurrentBranchAsync(folder);
                project.IsDirty = await _gitService.IsDirtyAsync(folder);
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

        InvalidateDevStartAttempt(project);

        // 先停行程，確認關閉後才從集合移除，避免 manager 仍追蹤舊行程時 UI 互斥判斷漏掉它
        await _frontendProcesses.CancelOneShotAsync(project);
        await _frontendProcesses.StopDevAsync(project);

        ProjectsOf(project.Group).Remove(project);
        UpdateFrontendRunningLabels();
        await SaveSettingsAsync();
        StatusMessage = $"已移除前端專案 {project.Name}";
    }

    private int BeginDevStartAttempt(FrontendProject project)
    {
        var next = _devStartAttempts.TryGetValue(project, out var current) ? current + 1 : 1;
        _devStartAttempts[project] = next;
        return next;
    }

    private int InvalidateDevStartAttempt(FrontendProject project)
        => _devStartAttempts[project] = _devStartAttempts.TryGetValue(project, out var current) ? current + 1 : 1;

    private bool IsCurrentDevStartAttempt(FrontendProject project, int attempt)
        => _devStartAttempts.TryGetValue(project, out var current) && current == attempt;

    private bool IsDevStartAttemptActive(FrontendProject project, int attempt)
        => IsCurrentDevStartAttempt(project, attempt) && _frontendProcesses.IsDevActive(project);

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
        _frontendProjectsLoaded = true; // 此後 SaveSettingsAsync 才可覆寫前端專案清單
        _ = RefreshFrontendBranchesAsync();
    }

    private async Task RefreshFrontendBranchesAsync()
    {
        foreach (var project in InternalProjects.Concat(ExternalProjects).ToList())
        {
            var isGitRepo = _gitService.IsGitRepository(project.FolderPath);
            await (Application.Current?.Dispatcher.InvokeAsync(() => project.IsGitRepo = isGitRepo).Task
                   ?? Task.CompletedTask);
            if (!isGitRepo) continue;
            try
            {
                var branch = await _gitService.GetCurrentBranchAsync(project.FolderPath);
                var isDirty = await _gitService.IsDirtyAsync(project.FolderPath);
                await (Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    project.CurrentBranch = branch;
                    project.IsDirty = isDirty;
                }).Task ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                // 單一 repo 讀取失敗（git 不在 PATH 等）不中止其餘專案的分支更新
                System.Diagnostics.Debug.WriteLine($"[Frontend] 讀取 {project.Name} 分支失敗: {ex.Message}");
            }
        }
    }
}
