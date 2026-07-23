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
        RuntimeImage = "mcr.microsoft.com/dotnet/sdk:10.0",
        SrcRoot = @"D:\CMPBackend"
    };

    [Fact]
    public void ContainerWorkingDir_取csproj的目錄()
    {
        Assert.Equal("/src/OrderBackend", FastDevComposeGenerator.ContainerWorkingDir(SampleConfig()));
    }

    [Fact]
    public void ContainerProjectPath_接在src下()
    {
        Assert.Equal("/src/OrderBackend/OrderBackend.csproj",
            FastDevComposeGenerator.ContainerProjectPath(SampleConfig()));
    }

    [Fact]
    public void GenerateOverrideYaml_含image掛載與watch進入點()
    {
        var yaml = FastDevComposeGenerator.GenerateOverrideYaml(
            "orderbackend", SampleConfig(), @"D:\CMPBackend", @"C:\Users\me\.nuget\packages");

        Assert.Contains("orderbackend:", yaml);
        Assert.Contains("image: mcr.microsoft.com/dotnet/sdk:10.0", yaml);
        Assert.Contains(@"- D:\CMPBackend:/src:rw", yaml);
        Assert.Contains(@"- C:\Users\me\.nuget\packages:/root/.nuget/packages:rw", yaml);
        Assert.Contains("working_dir: /src/OrderBackend", yaml);
        Assert.Contains("DOTNET_USE_POLLING_FILE_WATCHER=1", yaml);
        Assert.Contains("\"dotnet\", \"watch\"", yaml);
        Assert.Contains("\"--non-interactive\"", yaml);
        Assert.Contains("\"/src/OrderBackend/OrderBackend.csproj\"", yaml);
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
