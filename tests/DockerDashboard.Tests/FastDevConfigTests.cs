using DockerDashboard.Models;
using Xunit;

namespace DockerDashboard.Tests;

public class FastDevConfigTests
{
    [Fact]
    public void AppSettings_預設值_FastDev欄位為空且SDK預設()
    {
        var settings = new AppSettings();
        Assert.Empty(settings.FastDevEnabledServiceKeys);
        Assert.Empty(settings.FastDevConfigs);
        Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", settings.DefaultSdkImage);
    }

    [Fact]
    public void FastDevConfig_可設定欄位()
    {
        var config = new FastDevConfig
        {
            ServiceKey = "path::orderbackend",
            CsprojRelativePath = "OrderBackend/OrderBackend.csproj",
            SdkImage = "mcr.microsoft.com/dotnet/sdk:10.0",
            SrcRoot = @"D:\CMPBackend"
        };
        Assert.Equal("orderbackend", config.ServiceKey.Split("::")[1]);
        Assert.Equal("OrderBackend/OrderBackend.csproj", config.CsprojRelativePath);
    }
}
