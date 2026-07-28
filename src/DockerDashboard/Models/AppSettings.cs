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
    public bool FastDevAutoReloadEnabled { get; set; } = true;
    // 傳統操作模式：關（預設）只露 Fast Dev；開了才顯示舊的普通啟動/重啟/完整重建等非 Fast Dev 操作
    public bool ClassicControlsEnabled { get; set; } = false;
    public int BuildKitParallelism { get; set; } = 0;
    public int StartupParallelism { get; set; } = 3;
    public bool AutoCheckUpdate { get; set; } = true;
    public List<string> FastDevEnabledServiceKeys { get; set; } = [];
    public List<FastDevConfig> FastDevConfigs { get; set; } = [];
    public string DefaultSdkImage { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";
    public string DefaultRuntimeImage { get; set; } = "mcr.microsoft.com/dotnet/aspnet:10.0";
}
