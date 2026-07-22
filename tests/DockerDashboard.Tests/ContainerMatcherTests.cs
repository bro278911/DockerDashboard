using DockerDashboard.Models;
using DockerDashboard.Services;

namespace DockerDashboard.Tests;

public class ContainerMatcherTests
{
    private static ContainerInfo Make(string name, string state, string project, string workdir, string svc) => new()
    {
        ID = name + "-id",
        Names = name,
        State = state,
        Labels = $"com.docker.compose.config-hash=abc,com.docker.compose.container-number=1," +
                 $"com.docker.compose.project.config_files={workdir}\\docker-compose.yml,{workdir}\\docker-compose.override.yml," +
                 $"com.docker.compose.project.working_dir={workdir}," +
                 $"com.docker.compose.project={project},com.docker.compose.service={svc}," +
                 "com.docker.compose.version=5.3.0,desktop.docker.io/ports.scheme=v2"
    };

    private static readonly HashSet<string> NoAmbiguous = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Resolve_ByWorkingDir_ExactFolder()
    {
        var container = Make("cmpbackend-nginx-1", "running", "cmpbackend", @"D:\proj\A", "nginx");
        var matcher = new ContainerMatcher([container]);

        var match = matcher.Resolve("nginx", null, @"D:\proj\A", "cmpbackend", NoAmbiguous);

        Assert.NotNull(match);
        Assert.Equal("cmpbackend-nginx-1", match.Names);
    }

    [Fact]
    public void Resolve_ByProjectName_WhenWorkingDirDiffers()
    {
        // 同一 compose project（name: cmpbackend）從別的 worktree 資料夾 up，
        // working_dir label 指向未匯入的資料夾，仍應以 project name 對上
        var container = Make("cmpbackend-orderbackend-1", "running", "cmpbackend",
            @"D:\proj\.worktrees\CMPBackend-feat-803", "orderbackend");
        var matcher = new ContainerMatcher([container]);

        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "orderbackend" };
        var match = matcher.Resolve("orderbackend", null, @"D:\proj\Code\CMPBackend", "cmpbackend", ambiguous);

        Assert.NotNull(match);
        Assert.Equal("cmpbackend-orderbackend-1", match.Names);
    }

    [Fact]
    public void Resolve_NoCrossMatch_WhenProjectNamesDiffer()
    {
        // 不同分支資料夾各自預設 project name（basename 衍生）不同，不得互相誤配
        var container = Make("appa-web-1", "running", "app-a", @"D:\proj\app-a", "web");
        var matcher = new ContainerMatcher([container]);

        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };
        var match = matcher.Resolve("web", null, @"D:\proj\app-b", "app-b", ambiguous);

        Assert.Null(match);
    }

    [Fact]
    public void Resolve_PrefersRunning_WhenSameKeyHasExitedDuplicate()
    {
        var exited = Make("cmpbackend-web-1-old", "exited", "cmpbackend", @"D:\proj\A", "web");
        var running = Make("cmpbackend-web-1", "running", "cmpbackend", @"D:\proj\B", "web");
        var matcher = new ContainerMatcher([exited, running]);

        var match = matcher.Resolve("web", null, @"D:\proj\C", "cmpbackend", NoAmbiguous);

        Assert.NotNull(match);
        Assert.Equal("running", match.State);
    }

    [Fact]
    public void Resolve_LooseMatchDisabled_ForAmbiguousServiceNames()
    {
        // 無 project、無 workdir 對應時，同名 service 跨資料夾不得寬鬆比對
        var container = new ContainerInfo
        {
            ID = "x",
            Names = "web",
            State = "running",
            Labels = "com.docker.compose.service=web"
        };
        var matcher = new ContainerMatcher([container]);

        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "web" };
        var match = matcher.Resolve("web", null, @"D:\proj\A", "", ambiguous);

        Assert.Null(match);
    }

    [Fact]
    public void Resolve_ByContainerName_TakesPriority()
    {
        var container = Make("my-fixed-name", "running", "cmpbackend", @"D:\proj\A", "web");
        var matcher = new ContainerMatcher([container]);

        var match = matcher.Resolve("web", "my-fixed-name", @"D:\other", "", NoAmbiguous);

        Assert.NotNull(match);
        Assert.Equal("my-fixed-name", match.Names);
    }

    [Fact]
    public void Resolve_WorkingDirParse_NotConfusedByConfigFilesCommas()
    {
        // config_files 值含逗號與路徑，working_dir 解析須以下一個 label 前綴為界
        var container = Make("cmpbackend-api-1", "running", "cmpbackend", @"D:\dir with,comma", "api");
        var matcher = new ContainerMatcher([container]);

        var match = matcher.Resolve("api", null, @"D:\dir with,comma", "", NoAmbiguous);

        Assert.NotNull(match);
    }
}
