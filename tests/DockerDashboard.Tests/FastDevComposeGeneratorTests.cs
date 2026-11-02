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

    private static string Block(string environment, AzureCredential? credential) =>
        FastDevComposeGenerator.ServiceOverrideBlock(
            "orderbackend", SampleConfig(), @"D:\CMPBackend\OrderBackend", @"C:\Users\me\.nuget\packages",
            environment, credential);

    private static List<string> ParsedEnvironment(string block)
    {
        var doc = new YamlDotNet.Serialization.Deserializer()
            .Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, object>>>>("services:\n" + block);
        return ((List<object>)doc["services"]["orderbackend"]["environment"]).Select(o => (string)o).ToList();
    }

    [Fact]
    public void ServiceOverrideBlock_Development輸出與舊版一致_即使傳入憑證也不寫AZURE()
    {
        var plain = FastDevComposeGenerator.ServiceOverrideBlock(
            "orderbackend", SampleConfig(), @"D:\CMPBackend\OrderBackend", @"C:\Users\me\.nuget\packages");

        Assert.Equal(plain, Block("Development", null));
        Assert.Equal(plain, Block("Development", new AzureCredential("t", "c", "s")));
        Assert.DoesNotContain("AZURE_", plain);
    }

    [Fact]
    public void ServiceOverrideBlock_Stage寫入環境與AZURE變數()
    {
        var env = ParsedEnvironment(Block("Stage", new AzureCredential("tenant-1", "client-1", "fake-secret")));

        Assert.Contains("ASPNETCORE_ENVIRONMENT=Stage", env);
        Assert.Contains("AZURE_TENANT_ID=tenant-1", env);
        Assert.Contains("AZURE_CLIENT_ID=client-1", env);
        Assert.Contains("AZURE_CLIENT_SECRET=fake-secret", env);
    }

    [Fact]
    public void ServiceOverrideBlock_Stage無憑證_只改環境()
    {
        var block = Block("Stage", null);

        Assert.Contains("ASPNETCORE_ENVIRONMENT=Stage", block);
        Assert.DoesNotContain("AZURE_", block);
    }

    [Fact]
    public void ServiceOverrideBlock_憑證含錢號單引號冒號_YAML可解析且錢號跳脫成雙錢號()
    {
        var env = ParsedEnvironment(Block("Production", new AzureCredential("t:1", "c#1", "ab$c'd:e")));

        Assert.Contains("ASPNETCORE_ENVIRONMENT=Production", env);
        Assert.Contains("AZURE_TENANT_ID=t:1", env);
        Assert.Contains("AZURE_CLIENT_ID=c#1", env);
        // compose 會把 $$ 還原成字面 $
        Assert.Contains("AZURE_CLIENT_SECRET=ab$$c'd:e", env);
    }
}
