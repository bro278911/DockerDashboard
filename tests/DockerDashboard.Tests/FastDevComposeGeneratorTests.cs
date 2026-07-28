using DockerDashboard.Models;
using DockerDashboard.Services;
using Xunit;

namespace DockerDashboard.Tests;

public class FastDevComposeGeneratorTests
{
    private static FastDevConfig SampleConfig() => new()
    {
        ServiceKey = "key::orderbackend",
        CsprojRelativePath = "OrderBackend/OrderBackend.csproj",
        RuntimeImage = "mcr.microsoft.com/dotnet/aspnet:10.0",
        Tfm = "net10.0",
        AssemblyName = "OrderBackend",
        SrcRoot = @"D:\CMPBackend"
    };

    [Fact]
    public void ContainerDllPath_指向app下的bin()
    {
        Assert.Equal("/app/bin/Debug/net10.0/OrderBackend.dll",
            FastDevComposeGenerator.ContainerDllPath(SampleConfig()));
    }

    [Fact]
    public void ProjectDirRelative_取csproj目錄()
    {
        Assert.Equal("OrderBackend", FastDevComposeGenerator.ProjectDirRelative(SampleConfig()));
    }

    [Fact]
    public void ServiceOverrideBlock_runtime掛bin跑dll且無services標頭()
    {
        var block = FastDevComposeGenerator.ServiceOverrideBlock(
            "orderbackend", SampleConfig(), @"D:\CMPBackend\OrderBackend", @"C:\Users\me\.nuget\packages");

        Assert.DoesNotContain("services:", block);
        Assert.Contains("  orderbackend:", block);
        Assert.Contains("image: orderbackend:fastdev", block);
        Assert.Contains("build:", block);
        Assert.Contains("target: base", block);
        Assert.Contains("working_dir: /app", block);
        Assert.Contains(@"- 'D:\CMPBackend\OrderBackend:/app:rw'", block);
        Assert.Contains(@"- 'C:\Users\me\.nuget\packages:/.nuget/packages:ro'", block);
        Assert.Contains("ASPNETCORE_ENVIRONMENT=Development", block);
        Assert.Contains("ASPNETCORE_STATICWEBASSETS=/app/__nostaticwebassets__.json", block);
        Assert.Contains("\"dotnet\", \"/app/bin/Debug/net10.0/OrderBackend.dll\"", block);
        Assert.Contains("\"--additionalProbingPath\", \"/.nuget/packages\"", block);
        Assert.DoesNotContain("watch", block);
    }

    [Fact]
    public void CombineOverride_一個services標頭串接多服務區塊()
    {
        var blockA = "  a:\n    image: x\n";
        var blockB = "  b:\n    image: y\n";

        var yaml = FastDevComposeGenerator.CombineOverride([blockA, blockB]);

        Assert.StartsWith("services:", yaml);
        Assert.Equal(1, yaml.Split("services:").Length - 1);
        Assert.Contains("  a:", yaml);
        Assert.Contains("  b:", yaml);
    }

    [Fact]
    public void ServiceOverrideBlock_路徑含空白與井號_以單引號包住()
    {
        var block = FastDevComposeGenerator.ServiceOverrideBlock(
            "orderbackend", SampleConfig(), @"D:\My Repo #1\OrderBackend", @"C:\Users\me\.nuget\packages");

        Assert.Contains(@"- 'D:\My Repo #1\OrderBackend:/app:rw'", block);
    }
}
