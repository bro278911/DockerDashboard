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
        Assert.Equal("mcr.microsoft.com/dotnet/aspnet:10.0", settings.DefaultRuntimeImage);
    }

    [Fact]
    public void FastDevConfig_可設定v2欄位()
    {
        var config = new FastDevConfig
        {
            ServiceKey = "path::orderbackend",
            CsprojRelativePath = "OrderBackend/OrderBackend.csproj",
            RuntimeImage = "mcr.microsoft.com/dotnet/aspnet:10.0",
            Tfm = "net10.0",
            AssemblyName = "OrderBackend",
            SrcRoot = @"D:\CMPBackend"
        };
        Assert.Equal("net10.0", config.Tfm);
        Assert.Equal("OrderBackend", config.AssemblyName);
        Assert.Equal("mcr.microsoft.com/dotnet/aspnet:10.0", config.RuntimeImage);
    }
}
