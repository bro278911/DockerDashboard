using System.IO;
using DockerDashboard.Services;

namespace DockerDashboard.Tests;

public class ProjectEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "projenv-" + Guid.NewGuid().ToString("N"));

    public ProjectEnvironmentTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private void AddService(string name, params string[] environments)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".csproj"), "<Project />");
        foreach (var env in environments)
            File.WriteAllText(Path.Combine(dir, $"appsettings.{env}.json"), "{}");
    }

    [Fact]
    public void 可選環境_只列實際存在的檔案_Production關閉時不列()
    {
        AddService("api", "Development", "Stage", "Production");

        Assert.Equal(["Development", "Stage"], ProjectEnvironment.DetectAvailable([_root], allowProduction: false));
    }

    [Fact]
    public void 可選環境_Production開啟且有檔案時列入()
    {
        AddService("api", "Stage", "Production");

        Assert.Equal(["Development", "Stage", "Production"], ProjectEnvironment.DetectAvailable([_root], allowProduction: true));
    }

    [Fact]
    public void 可選環境_任一服務有檔案即列入()
    {
        AddService("api", "Development");
        AddService("orders", "Stage");

        Assert.Equal(["Development", "Stage"], ProjectEnvironment.DetectAvailable([_root], allowProduction: true));
    }

    [Fact]
    public void 可選環境_都沒有檔案時只剩Development()
    {
        AddService("api");

        Assert.Equal(["Development"], ProjectEnvironment.DetectAvailable([_root], allowProduction: true));
    }

    [Fact]
    public void 設定缺項_預設Development()
    {
        Assert.Equal("Development", ProjectEnvironment.GetStored(new Dictionary<string, string>(), @"D:\proj"));
        Assert.Equal("Development", ProjectEnvironment.GetStored(null, @"D:\proj"));
    }

    [Fact]
    public void 設定讀取_路徑不分大小寫_未知值視為Development()
    {
        var map = new Dictionary<string, string>
        {
            [@"D:\PROJ"] = "stage",
            [@"D:\other"] = "Staging",
        };

        Assert.Equal("Stage", ProjectEnvironment.GetStored(map, @"d:\proj"));
        Assert.Equal("Development", ProjectEnvironment.GetStored(map, @"D:\other"));
    }

    [Fact]
    public void 設定寫入_Development不存檔_換大小寫路徑不產生重複key()
    {
        var map = new Dictionary<string, string>();

        ProjectEnvironment.Set(map, @"D:\PROJ", "Stage");
        ProjectEnvironment.Set(map, @"d:\proj", "Production");
        Assert.Equal("Production", Assert.Single(map).Value);

        ProjectEnvironment.Set(map, @"D:\proj", "Development");
        Assert.Empty(map);
    }
}
