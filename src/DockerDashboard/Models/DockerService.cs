using CommunityToolkit.Mvvm.ComponentModel;
using DockerDashboard.Services;

namespace DockerDashboard.Models;

public partial class DockerService : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _image = string.Empty;

    [ObservableProperty]
    private string _containerId = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    private ContainerStatus _status = ContainerStatus.Unknown;

    [ObservableProperty]
    private string _ports = string.Empty;

    [ObservableProperty]
    private string _containerName = string.Empty;

    public bool IsRunning => Status == ContainerStatus.Running || Status == ContainerStatus.Restarting;
    public bool IsStopped => Status != ContainerStatus.Running && Status != ContainerStatus.Restarting;

    [ObservableProperty]
    private bool _isFastDev;

    // 此服務容器實際套用的 ASPNETCORE_ENVIRONMENT（執行期狀態，由設定還原、啟動成功時更新）；與專案選定環境不同即待重啟
    public string AppliedEnvironment { get; set; } = ProjectEnvironment.Development;

    public string ComposeFilePath { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    // compose build.dockerfile 解析出的絕對路徑；Fast Dev 據此確定對應哪個專案（對齊 VS，不靠資料夾名猜）
    public string DockerfilePath { get; set; } = string.Empty;

    // compose depends_on 的服務名稱（同一個 compose 專案內）；單服務啟動時據此帶起未執行的依賴
    public List<string> DependsOn { get; set; } = [];

    public string WatchKey => $"{ComposeFilePath}::{Name}";
}
