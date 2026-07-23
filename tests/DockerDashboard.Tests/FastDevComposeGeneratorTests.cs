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
    public void GenerateOverrideYaml_runtime掛bin跑dll()
    {
        var yaml = FastDevComposeGenerator.GenerateOverrideYaml(
            "orderbackend", SampleConfig(), @"D:\CMPBackend\OrderBackend", @"C:\Users\me\.nuget\packages");

        Assert.Contains("orderbackend:", yaml);
        Assert.Contains("image: mcr.microsoft.com/dotnet/aspnet:10.0", yaml);
        Assert.Contains(@"- D:\CMPBackend\OrderBackend:/app:rw", yaml);
        Assert.Contains(@"- C:\Users\me\.nuget\packages:/root/.nuget/packages:ro", yaml);
        Assert.Contains("ASPNETCORE_ENVIRONMENT=Development", yaml);
        Assert.Contains("\"dotnet\", \"/app/bin/Debug/net10.0/OrderBackend.dll\"", yaml);
        Assert.Contains("\"--additionalProbingPath\", \"/root/.nuget/packages\"", yaml);
        Assert.DoesNotContain("watch", yaml);
    }

    [Fact]
    public void BuildUpArgs_有override時_fastdev檔接在原檔後面()
    {
        var args = FastDevComposeGenerator.BuildUpArgs(
            ["compose"], ["-f", "docker-compose.yml"], @"C:\appdata\fd.yml", "orderbackend");

        Assert.Equal(
            ["compose", "-f", "docker-compose.yml", "-f", @"C:\appdata\fd.yml", "up", "-d", "--no-deps", "orderbackend"],
            args);
    }

    [Fact]
    public void BuildUpArgs_無override時_不加額外f()
    {
        var args = FastDevComposeGenerator.BuildUpArgs(
            ["compose"], ["-f", "docker-compose.yml"], null, "orderbackend");

        Assert.Equal(
            ["compose", "-f", "docker-compose.yml", "up", "-d", "--no-deps", "orderbackend"],
            args);
    }
}
