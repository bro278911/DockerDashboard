namespace DockerDashboard.Models;

public enum ContainerStatus
{
    Unknown,
    Running,
    Stopped,
    Paused,
    Restarting,
    Exited,
    Dead
}
