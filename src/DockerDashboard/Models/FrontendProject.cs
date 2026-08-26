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
    public static FrontendGroup? GuessGroupFromPath(string folderPath)
    {
        var containsMeso = folderPath.Contains("meso", StringComparison.OrdinalIgnoreCase);
        var containsStrato = folderPath.Contains("strato", StringComparison.OrdinalIgnoreCase);

        return (containsMeso, containsStrato) switch
        {
            (true, false) => FrontendGroup.Internal,
            (false, true) => FrontendGroup.External,
            _ => null,
        };
    }

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
    [NotifyPropertyChangedFor(nameof(BranchDisplay))]
    private string _currentBranch = string.Empty;

    // dev server 狀態（Stopped/Running/Crashed）。不受一次性指令影響，避免 install/vitest/e2e
    // 結束時把 dev server 的 Crashed 覆寫掉，或 dev 崩潰時把 IsBusy 誤判為 false（見 IsOneShotRunning）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayStatus))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private FrontendStatus _status = FrontendStatus.Stopped;

    // dev server 行程存活與否（一次性指令期間 dev 可能同時在跑，Status 不足以判斷，故獨立追蹤）
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDevStopped))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isDevRunning;

    // 一次性指令（install/vitest/e2e）執行中與否，獨立於 dev server 的 Status
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyPropertyChangedFor(nameof(DisplayStatus))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isOneShotRunning;

    // 啟動流程進行中（互斥確認、讀分支等 await 期間）。IsDevRunning 要到行程真的建立才會 true，
    // 這段空窗若不算入 IsIdle，使用者可在啟動途中把組別改掉，導致同組互斥與 log 歸屬錯亂
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isStarting;

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

    // 狀態文字：狀態燈只有顏色，色覺障礙者與輔助技術讀不到，故一併提供文字
    public string StatusText => DisplayStatus switch
    {
        FrontendStatus.Running => "執行中",
        FrontendStatus.Busy => "指令執行中",
        FrontendStatus.Crashed => "異常結束",
        _ => "已停止"
    };

    // 分支顯示：非 git 資料夾的 CurrentBranch 是空字串，直接綁會留白
    public string BranchDisplay => string.IsNullOrEmpty(CurrentBranch) ? "非 git" : CurrentBranch;

    // 沒有任何行程在跑、也不在啟動途中才可編輯：組別會決定行程追蹤、log 歸屬與同組互斥
    public bool IsIdle => !IsDevRunning && !IsOneShotRunning && !IsStarting;

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
