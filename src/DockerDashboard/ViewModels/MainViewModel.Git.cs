using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DockerDashboard.Models;
using Application = System.Windows.Application;

namespace DockerDashboard.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private async Task SwitchBranchAsync(DockerProject? project)
    {
        if (project == null || !project.IsGitRepo) return;
        if (IsBusy)
        {
            StatusMessage = "⚠ 已有操作進行中，請稍候";
            return;
        }

        if (project.IsDirty)
        {
            var confirm = System.Windows.MessageBox.Show(
                $"專案 {project.Name} 有未提交的變更，切換分支可能導致衝突。\n\n確定要繼續嗎？",
                "未提交變更警告",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (confirm != System.Windows.MessageBoxResult.Yes) return;
        }

        // 抓分支清單就開始 await，先佔住旗標，避免這段期間其他操作插隊並行
        IsOperating = true;
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
            AppendLog($"[{DateTime.Now:HH:mm:ss}] 🔀 切換 {project.Name} → {targetBranch}");

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

    [RelayCommand]
    private async Task PullBackendAsync(DockerProject? project)
    {
        if (project == null || !project.IsGitRepo) return;
        if (IsBusy)
        {
            StatusMessage = "⚠ 已有操作進行中，請稍候";
            return;
        }

        IsOperating = true;
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
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⬇ Pull {project.Name} ← {remoteBranch}");

            var result = await _gitService.PullAsync(project.FolderPath, remoteBranch);

            if (result.BlockedByDirty)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ 有未提交變更，請先 commit 或 stash");
                StatusMessage = $"⚠ {project.Name} 有未提交變更，請先 commit 或 stash";
                System.Windows.MessageBox.Show(
                    $"專案 {project.Name} 有未提交的變更，請先 commit 或 stash 後再 Pull。",
                    "有未提交變更",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.Output))
                AppendLog($"[{DateTime.Now:HH:mm:ss}] {result.Output.Trim()}");

            project.CurrentBranch = await _gitService.GetCurrentBranchAsync(project.FolderPath);
            project.IsDirty = await _gitService.IsDirtyAsync(project.FolderPath);

            if (result.Success)
            {
                StatusMessage = $"✅ {project.Name} pull 成功";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✔ Pull 成功");
            }
            else if (result.Restored)
            {
                var shortSha = result.PreviousSha is { Length: >= 7 } sha ? sha[..7] : result.PreviousSha;
                StatusMessage = $"❌ {project.Name} pull 失敗，已還原至 {shortSha}";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✖ Pull 失敗，已還原至 {shortSha}");
            }
            else
            {
                StatusMessage = $"❌ {project.Name} pull 失敗，還原失敗: {result.RestoreError}";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✖ Pull 失敗，還原失敗: {result.RestoreError}");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ {project.Name} pull 失敗";
            AppendLog($"[例外] {ex.Message}");
        }
        finally
        {
            IsOperating = false;
        }
    }
}
