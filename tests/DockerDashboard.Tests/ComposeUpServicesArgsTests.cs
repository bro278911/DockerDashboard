using System.IO;
using DockerDashboard.Models;
using DockerDashboard.Services;

namespace DockerDashboard.Tests;

public class ComposeUpServicesArgsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cus-" + Guid.NewGuid().ToString("N"));

    public ComposeUpServicesArgsTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "docker-compose.yml"), "services: {}");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void 參數含_up_build_no_deps_與全部服務名()
    {
        var cli = new DockerCliService();

        var args = cli.BuildComposeUpServicesArgs(_dir, ["api", "nginx"], null);

        var upIndex = args.IndexOf("up");
        Assert.Equal(["up", "-d", "--build", "--no-deps", "api", "nginx"], args.Skip(upIndex).ToArray());
        Assert.DoesNotContain("--remove-orphans", args);
    }

    [Fact]
    public void 有_override_時帶_f_且排在_up_之前()
    {
        var cli = new DockerCliService();

        var args = cli.BuildComposeUpServicesArgs(_dir, ["api"], @"C:\o\fast.yml");

        var overrideIndex = args.IndexOf(@"C:\o\fast.yml");
        Assert.True(overrideIndex > 0);
        Assert.Equal("-f", args[overrideIndex - 1]);
        Assert.True(overrideIndex < args.IndexOf("up"));
    }

    [Fact]
    public void WSL2_模式_override_路徑轉成_mnt()
    {
        var cli = new DockerCliService { DockerMode = DockerMode.Wsl2 };

        var args = cli.BuildComposeUpServicesArgs(_dir, ["api"], @"C:\o\fast.yml");

        Assert.Contains("/mnt/c/o/fast.yml", args);
    }
}
