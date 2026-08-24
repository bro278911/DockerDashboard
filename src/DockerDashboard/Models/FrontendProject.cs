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

    // dev server 狀態（Stopped/Running/Crashed）。不受一次性指令影響，避免 install/vitest/e2e
    // 結束時把 dev server 的 Crashed 覆寫掉，或 dev 崩潰時把 IsBusy 誤判為 false（見 IsOneShotRunning）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatus))]
    private FrontendStatus _status = FrontendStatus.Stopped;

    // dev server 行程存活與否（一次性指令期間 dev 可能同時在跑，Status 不足以判斷，故獨立追蹤）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDevStopped))]
    private bool _isDevRunning;

    // 一次性指令（install/vitest/e2e）執行中與否，獨立於 dev server 的 Status
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyPropertyChangedFor(nameof(DisplayStatus))]
    private bool _isOneShotRunning;

    // 磁碟根目錄（如 D:\）GetFileName 回傳空字串，回退顯示完整路徑避免留白
    public string FolderName
    {
        get
        {
            var name = System.IO.Path.GetFileName(FolderPath.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? FolderPath : name;
        }
    }
    public bool IsDevStopped => !IsDevRunning;
    public bool IsBusy => IsOneShotRunning;
    public bool IsNotBusy => !IsOneShotRunning;

    // UI 顯示用：一次性指令執行中時優先顯示 Busy（黃燈），否則反映 dev server 的實際 Status
    public FrontendStatus DisplayStatus => IsOneShotRunning ? FrontendStatus.Busy : Status;

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
