using CommunityToolkit.Mvvm.ComponentModel;

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
    private bool _isWatching;

    public string ComposeFilePath { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string WatchKey => $"{ComposeFilePath}::{Name}";
}
