using System.Collections.Generic;

namespace DockerDashboard.Models;

public enum DockerMode
{
    DockerDesktop,
    Wsl2
}

public class AppSettings
{
    public List<string> ImportedFolders { get; set; } = [];
    public List<string> RecentlyRemovedFolders { get; set; } = [];
    public int PollIntervalSeconds { get; set; } = 5;
    public bool UseComposeV2 { get; set; } = true;
    public DockerMode DockerMode { get; set; } = DockerMode.DockerDesktop;
    public string WslDistroName { get; set; } = "Ubuntu";
    public bool AutoWatchEnabled { get; set; } = false;
    public int WatchDebounceSeconds { get; set; } = 2;
    public List<string> WatchEnabledServiceKeys { get; set; } = [];
    public int BuildKitParallelism { get; set; } = 0;
    public bool AutoCheckUpdate { get; set; } = true;
}
