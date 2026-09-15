using DockerDashboard.Models;
using DockerDashboard.ViewModels;

namespace DockerDashboard.Tests;

public class MainViewModelFastDevStateTests
{
    [Fact]
    public void ShouldEnableFastDev_有殘留標記但服務已停止時_仍需啟動()
    {
        var service = new DockerService
        {
            IsFastDev = true,
            Status = ContainerStatus.Stopped
        };

        Assert.True(MainViewModel.ShouldEnableFastDev(service));
    }

    [Fact]
    public void ShouldEnableFastDev_FastDev容器正在執行時_不重複啟動()
    {
        var service = new DockerService
        {
            IsFastDev = true,
            Status = ContainerStatus.Running
        };

        Assert.False(MainViewModel.ShouldEnableFastDev(service));
    }

    [Fact]
    public void ShouldEnableFastDev_FastDev容器狀態未確認時_不重複啟動()
    {
        var service = new DockerService
        {
            IsFastDev = true,
            Status = ContainerStatus.Unknown
        };

        Assert.False(MainViewModel.ShouldEnableFastDev(service));
    }

    [Fact]
    public void PersistFastDev_啟用時加入清單與設定()
    {
        var settings = new AppSettings();
        var config = new FastDevConfig { ServiceKey = "compose.yml::api" };

        MainViewModel.PersistFastDev(settings, "compose.yml::api", config, true);

        Assert.Contains("compose.yml::api", settings.FastDevEnabledServiceKeys);
        Assert.Contains(settings.FastDevConfigs, c => c.ServiceKey == "compose.yml::api");
    }

    [Fact]
    public void PersistFastDev_停用時移除清單與設定()
    {
        var settings = new AppSettings
        {
            FastDevEnabledServiceKeys = ["compose.yml::api"],
            FastDevConfigs = [new FastDevConfig { ServiceKey = "compose.yml::api" }],
        };

        MainViewModel.PersistFastDev(settings, "compose.yml::api", null, false);

        Assert.DoesNotContain("compose.yml::api", settings.FastDevEnabledServiceKeys);
        Assert.Empty(settings.FastDevConfigs);
    }
}
