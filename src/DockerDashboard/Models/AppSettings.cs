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
    public bool AutoCheckUpdate { get; set; } = true;
    public List<string> FastDevEnabledServiceKeys { get; set; } = [];
    public List<FastDevConfig> FastDevConfigs { get; set; } = [];
    public string DefaultSdkImage { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";
    public string DefaultRuntimeImage { get; set; } = "mcr.microsoft.com/dotnet/aspnet:10.0";
    public List<FrontendProjectConfig> FrontendProjects { get; set; } = [];
    // 專案資料夾路徑 → ASPNETCORE_ENVIRONMENT（缺項 = Development；key 以不分大小寫比對，見 ProjectEnvironment）
    public Dictionary<string, string> ProjectEnvironments { get; set; } = [];
    // 預設關：未開啟前，介面不提供 Production 選項，已存的 Production 設定載入時回落 Development
    public bool AllowProductionEnvironment { get; set; } = false;
}
