using DockerDashboard.Models;
using DockerDashboard.ViewModels;

namespace DockerDashboard.Tests;

public class MainViewModelFastDevStateTests
{
    [Fact]
    public void PersistFastDev_啟用時移除Watch設定()
    {
        var settings = new AppSettings
        {
            WatchEnabledServiceKeys = ["compose.yml::api"],
        };

        MainViewModel.PersistFastDev(settings, "compose.yml::api", null, true);

        Assert.DoesNotContain("compose.yml::api", settings.WatchEnabledServiceKeys);
        Assert.Contains("compose.yml::api", settings.FastDevEnabledServiceKeys);
    }

    [Fact]
    public void CanEnableWatch_FastDev啟用時回傳False()
    {
        var service = new DockerService
        {
            IsFastDev = true,
        };

        Assert.False(MainViewModel.CanEnableWatch(service));
    }

    [Fact]
    public void ShouldRestoreWatch_兩種設定同時存在時FastDev優先()
    {
        var settings = new AppSettings
        {
            WatchEnabledServiceKeys = ["compose.yml::api"],
            FastDevEnabledServiceKeys = ["compose.yml::api"],
        };

        Assert.False(MainViewModel.ShouldRestoreWatch(settings, "compose.yml::api"));
    }
}
