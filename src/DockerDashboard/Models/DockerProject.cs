using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DockerDashboard.Services;

namespace DockerDashboard.Models;

public partial class DockerProject : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private bool _isGitRepo;

    [ObservableProperty]
    private string _currentBranch = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    // 此專案所有 .NET 服務共用的 ASPNETCORE_ENVIRONMENT
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnvironmentChoices), nameof(IsStageEnvironment), nameof(IsProductionEnvironment))]
    private string _selectedEnvironment = ProjectEnvironment.Development;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnvironmentChoices))]
    private IReadOnlyList<string> _availableEnvironments = [ProjectEnvironment.Development];

    // 環境已選定但在跑的容器還是舊環境（使用者拒絕重啟或重啟失敗），UI 須標示，避免誤判實際連線目標；
    // 只由 MainViewModel.RefreshEnvironmentPending 依各服務實際套用的環境計算
    [ObservableProperty]
    private bool _environmentPendingRestart;

    // 可選環境掃描的世代號：掃描期間又有新的掃描開始，舊結果就不能再套用
    internal int EnvironmentScanVersion { get; set; }

    public bool IsStageEnvironment => SelectedEnvironment == ProjectEnvironment.Stage;
    public bool IsProductionEnvironment => SelectedEnvironment == ProjectEnvironment.Production;

    // 右鍵「環境」子選單的項目
    public IReadOnlyList<EnvironmentChoice> EnvironmentChoices =>
        [.. AvailableEnvironments.Select(e => new EnvironmentChoice(this, e, e == SelectedEnvironment))];

    // 在跑的服務數（只算 Running，Restarting 不算），由 RefreshRunningCounts 重算
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFullyRunning), nameof(IsPartiallyRunning), nameof(RunningSummary))]
    private int _runningServiceCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFullyRunning), nameof(IsPartiallyRunning), nameof(RunningSummary))]
    private int _totalServiceCount;

    public bool IsFullyRunning => TotalServiceCount > 0 && RunningServiceCount == TotalServiceCount;
    public bool IsPartiallyRunning => RunningServiceCount > 0 && RunningServiceCount < TotalServiceCount;
    public string RunningSummary => $"{RunningServiceCount}/{TotalServiceCount}";

    public ObservableCollection<ComposeFile> ComposeFiles { get; } = [];

    public void RefreshRunningCounts()
    {
        var services = ComposeFiles.SelectMany(c => c.Services).ToList();
        TotalServiceCount = services.Count;
        RunningServiceCount = services.Count(s => s.Status == ContainerStatus.Running);
    }
}

public sealed record EnvironmentChoice(DockerProject Project, string Name, bool IsCurrent)
{
    public string DisplayName => IsCurrent ? $"✔ {Name}" : Name;
}

public partial class ComposeFile : ObservableObject
{
    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _directoryPath = string.Empty;

    // compose project name（config 的 name 欄位，未明訂時為資料夾名衍生預設值）
    public string ProjectName { get; set; } = string.Empty;

    public ObservableCollection<DockerService> Services { get; } = [];
}
