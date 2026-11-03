using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DockerDashboard.Models;
using DockerDashboard.Services;

namespace DockerDashboard.ViewModels;

// 依專案選擇 ASPNETCORE_ENVIRONMENT（Development／Stage／Production）
public partial class MainViewModel
{
    private bool _allowProductionEnvironment;
    private int _environmentScanCounter;

    // 上方狀態區的紅色 Production 警示
    public bool SelectedProjectIsProduction => SelectedProject?.IsProductionEnvironment == true;

    // 切到 Production、以 Production 啟動共用同一個確認框；測試可替換
    internal Func<DockerProject, bool> ConfirmProductionEnvironment { get; set; } = project =>
        System.Windows.MessageBox.Show(
            $"「{project.Name}」即將以 Production 環境執行。\n\n" +
            "本機程式會連線到正式資料（資料庫、儲存體）與外部服務，操作可能直接影響正式環境。要繼續嗎？",
            "Production 環境確認", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No)
        == System.Windows.MessageBoxResult.Yes;

    internal Func<DockerProject, bool> ConfirmRestartForEnvironment { get; set; } = project =>
        System.Windows.MessageBox.Show(
            $"「{project.Name}」的環境已變更，需重新啟動才會生效（會重建正在執行的 .NET 服務容器）。\n\n要現在重新啟動嗎？",
            "環境已切換", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
        == System.Windows.MessageBoxResult.Yes;

    private DockerProject? ProjectOf(DockerService service) =>
        Projects.FirstOrDefault(p => p.ComposeFiles.Any(c => c.Services.Contains(service)));

    private string EnvironmentOf(DockerService service) =>
        ProjectOf(service)?.SelectedEnvironment ?? ProjectEnvironment.Development;

    // 載入／設定變更時由設定還原各專案的環境；Production 未開放時回落 Development（設定檔值不動）
    internal void ApplyProjectEnvironments(AppSettings settings)
    {
        _allowProductionEnvironment = settings.AllowProductionEnvironment;
        foreach (var project in Projects)
        {
            var stored = ProjectEnvironment.GetStored(settings.ProjectEnvironments, project.FolderPath);
            if (stored == ProjectEnvironment.Production && !settings.AllowProductionEnvironment)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ {project.Name} 設定為 Production 但「允許 Production 環境」未開啟，改用 Development");
                stored = ProjectEnvironment.Development;
            }
            project.SelectedEnvironment = stored;
            RefreshEnvironmentPending(project);
        }
        OnPropertyChanged(nameof(SelectedProjectIsProduction));
    }

    // 偵測要讀檔掃目錄，放背景執行緒；完成後才更新選單，不拖慢清單顯示
    internal async Task RefreshAvailableEnvironmentsAsync(IReadOnlyList<DockerProject>? projects = null)
    {
        try
        {
            var targets = (projects ?? [.. Projects]).Select(project => (
                Project: project,
                Dirs: project.ComposeFiles
                    .SelectMany(c => c.Services.Select(s => s.WorkingDirectory).Append(c.DirectoryPath))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList())).ToList();
            var allow = _allowProductionEnvironment;
            var versions = targets.Select(t => t.Project.EnvironmentScanVersion = ++_environmentScanCounter).ToList();

            var detected = await Task.Run(() =>
                targets.Select(t => ProjectEnvironment.DetectAvailable(t.Dirs, allow)).ToList());

            // 掃描期間「允許 Production」被改過，結果可能含已不開放的選項（或漏掉剛開放的），整批丟棄
            if (allow != _allowProductionEnvironment) return;

            for (var i = 0; i < targets.Count; i++)
            {
                // 同一專案已有較新的掃描就丟棄這份舊結果
                if (targets[i].Project.EnvironmentScanVersion != versions[i]) continue;
                var list = detected[i];
                // 目前選中的環境一定要在清單內，否則選單看不到現況
                if (!list.Contains(targets[i].Project.SelectedEnvironment))
                    list.Add(targets[i].Project.SelectedEnvironment);
                targets[i].Project.AvailableEnvironments = list;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⚠ 偵測可選環境失敗: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SelectProjectEnvironmentAsync(EnvironmentChoice? choice)
    {
        if (choice == null) return;
        var project = choice.Project;
        var target = choice.Name;
        if (project.SelectedEnvironment == target) return;
        if (IsBusy)
        {
            StatusMessage = "⚠ 已有操作進行中，請稍候";
            return;
        }

        // 選單已不列 Production，但舊選單項目或掃描結果仍可能送進來；未開放時不能靠確認框放行
        if (target == ProjectEnvironment.Production && !_allowProductionEnvironment)
        {
            StatusMessage = "⚠ 未開啟「允許 Production 環境」，無法切換至 Production";
            return;
        }

        if (target == ProjectEnvironment.Production && !ConfirmProductionEnvironment(project))
        {
            StatusMessage = $"已取消 {project.Name} 切換至 Production";
            return;
        }

        project.SelectedEnvironment = target;
        OnPropertyChanged(nameof(SelectedProjectIsProduction));
        await _settingsService.UpdateAsync(s => ProjectEnvironment.Set(s.ProjectEnvironments, project.FolderPath, target));
        AppendLog($"[{DateTime.Now:HH:mm:ss}] 🌐 {project.Name} 環境切換為 {target}");

        // 只有 Fast Dev 的 .NET 服務吃這個環境；一般 image 的服務重啟也不會變。
        // 只重啟套用中環境與新環境不同的服務
        RefreshEnvironmentPending(project);
        var running = RunningFastDev(project).Where(s => s.AppliedEnvironment != target).ToList();
        if (running.Count == 0)
        {
            StatusMessage = $"{project.Name} 環境已設為 {target}，下次啟動生效";
            return;
        }

        if (!ConfirmRestartForEnvironment(project))
        {
            StatusMessage = $"{project.Name} 環境已設為 {target}，重新啟動後生效";
            return;
        }

        // 使用者剛在上面確認過環境，啟動時不再重問
        // 一次取消代表整批取消：前一個目錄被取消就不再重啟後續目錄
        _lastOperationCancelled = false;
        foreach (var group in running.GroupBy(s => s.WorkingDirectory, StringComparer.OrdinalIgnoreCase))
        {
            await StartServicesAsync(group.Key, group.ToList(), environmentConfirmed: true);
            if (_lastOperationCancelled) break;
        }
    }

    private static List<DockerService> RunningFastDev(DockerProject project) =>
        ProjectServices(project).Where(s => s.IsFastDev && s.IsRunning).ToList();

    // 待重啟 = 專案內任一在跑的 Fast Dev 服務，容器實際套用的環境與專案目前選定的不同
    // （各服務逐一記錄，所以跨目錄分批重啟、重開 App 後都算得出來）
    private static void RefreshEnvironmentPending(DockerProject project) =>
        project.EnvironmentPendingRestart = RunningFastDev(project)
            .Any(s => s.AppliedEnvironment != project.SelectedEnvironment);

    // 以 Production 啟動前的二次確認；任一專案被拒絕即中止
    private bool ConfirmProductionStart(IReadOnlyList<DockerService> scope)
    {
        var productionProjects = scope
            .Select(ProjectOf)
            .OfType<DockerProject>()
            .Where(p => p.IsProductionEnvironment)
            .Distinct();
        return productionProjects.All(ConfirmProductionEnvironment);
    }

    // compose 的錯誤輸出可能原樣回顯 override 的內容行，寫進日誌前先遮蔽憑證值
    internal static string RedactSecrets(string line, IEnumerable<string> secrets)
    {
        foreach (var secret in secrets)
            line = line.Replace(secret, "***", StringComparison.Ordinal);
        return line;
    }

    private static string HostProjectDir(FastDevConfig config)
    {
        var projectDir = FastDevComposeGenerator.ProjectDirRelative(config);
        return projectDir.Length == 0
            ? config.SrcRoot
            : System.IO.Path.Combine(config.SrcRoot, projectDir.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }
}
