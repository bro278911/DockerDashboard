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
}
