using CommunityToolkit.Mvvm.ComponentModel;

namespace DockerDashboard.Models;

public enum FrontendGroup
{
    Internal,
    External
}

public enum FrontendStatus
{
    Stopped,
    Running,
    Busy,     // 一次性指令執行中
    Crashed
}

public partial class FrontendProject : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderName))]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private FrontendGroup _group;

    [ObservableProperty]
    private string _devCommand = "npm run dev";

    [ObservableProperty]
    private string _installCommand = "npm install";

    [ObservableProperty]
    private string _testCommand = "npm run test";

    [ObservableProperty]
    private string _e2eCommand = "npx playwright test";

    [ObservableProperty]
    private string _currentBranch = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private FrontendStatus _status = FrontendStatus.Stopped;

    // dev server 行程存活與否（Busy 期間 dev 可能同時在跑，Status 不足以判斷，故獨立追蹤）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDevStopped))]
    private bool _isDevRunning;

    public string FolderName => System.IO.Path.GetFileName(FolderPath.TrimEnd('\\', '/'));
    public bool IsDevStopped => !IsDevRunning;
    public bool IsBusy => Status == FrontendStatus.Busy;
    public bool IsNotBusy => Status != FrontendStatus.Busy;

    public static FrontendProject FromConfig(FrontendProjectConfig config) => new()
    {
        Name = config.Name,
        FolderPath = config.FolderPath,
        Group = config.Group,
        DevCommand = config.DevCommand,
        InstallCommand = config.InstallCommand,
        TestCommand = config.TestCommand,
        E2eCommand = config.E2eCommand,
    };

    public FrontendProjectConfig ToConfig() => new()
    {
        Name = Name,
        FolderPath = FolderPath,
        Group = Group,
        DevCommand = DevCommand,
        InstallCommand = InstallCommand,
        TestCommand = TestCommand,
        E2eCommand = E2eCommand,
    };
}

public class FrontendProjectConfig
{
    public string Name { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public FrontendGroup Group { get; set; }
    public string DevCommand { get; set; } = "npm run dev";
    public string InstallCommand { get; set; } = "npm install";
    public string TestCommand { get; set; } = "npm run test";
    public string E2eCommand { get; set; } = "npx playwright test";
}
