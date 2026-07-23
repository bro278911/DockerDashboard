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
        Assert.Contains("image: mcr.microsoft.com/dotnet/aspnet:10.0", block);
        Assert.Contains(@"- D:\CMPBackend\OrderBackend:/app:rw", block);
        Assert.Contains(@"- C:\Users\me\.nuget\packages:/root/.nuget/packages:ro", block);
        Assert.Contains("ASPNETCORE_ENVIRONMENT=Development", block);
        Assert.Contains("\"dotnet\", \"/app/bin/Debug/net10.0/OrderBackend.dll\"", block);
        Assert.Contains("\"--additionalProbingPath\", \"/root/.nuget/packages\"", block);
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
    public void BuildUpAllArgs_有override時_fastdev檔接在原檔後且不帶service()
    {
        var args = FastDevComposeGenerator.BuildUpAllArgs(
            ["compose"], ["-f", "docker-compose.yml"], @"C:\appdata\fd.yml");

        Assert.Equal(
            ["compose", "-f", "docker-compose.yml", "-f", @"C:\appdata\fd.yml", "up", "-d"],
            args);
    }

    [Fact]
    public void BuildUpAllArgs_無override時_不加額外f()
    {
        var args = FastDevComposeGenerator.BuildUpAllArgs(
            ["compose"], ["-f", "docker-compose.yml"], null);

        Assert.Equal(["compose", "-f", "docker-compose.yml", "up", "-d"], args);
    }
}
