using DockerDashboard.Models;
using DockerDashboard.ViewModels;

namespace DockerDashboard.Tests;

public class RunningIndicatorTests
{
    private static DockerProject CreateProject(
        string name, int running, int restarting = 0, int stopped = 0,
        bool isGitRepo = true, string branch = "dev")
    {
        var project = new DockerProject { Name = name, IsGitRepo = isGitRepo, CurrentBranch = branch };
        var compose = new ComposeFile();
        for (var i = 0; i < running; i++) compose.Services.Add(new DockerService { Name = $"r{i}", Status = ContainerStatus.Running });
        for (var i = 0; i < restarting; i++) compose.Services.Add(new DockerService { Name = $"x{i}", Status = ContainerStatus.Restarting });
        for (var i = 0; i < stopped; i++) compose.Services.Add(new DockerService { Name = $"s{i}", Status = ContainerStatus.Stopped });
        if (compose.Services.Count > 0) project.ComposeFiles.Add(compose);
        project.RefreshRunningCounts();
        return project;
    }

    [Fact]
    public void RefreshRunningCounts_全部在跑_IsFullyRunning()
    {
        var project = CreateProject("A", running: 11);

        Assert.True(project.IsFullyRunning);
        Assert.False(project.IsPartiallyRunning);
        Assert.Equal("11/11", project.RunningSummary);
    }

    [Fact]
    public void RefreshRunningCounts_Restarting不算在跑()
    {
        var project = CreateProject("A", running: 10, restarting: 1);

        Assert.True(project.IsPartiallyRunning);
        Assert.Equal("10/11", project.RunningSummary);
    }

    [Fact]
    public void RefreshRunningCounts_全停_不亮燈()
    {
        var project = CreateProject("A", running: 0, stopped: 11);

        Assert.False(project.IsFullyRunning);
        Assert.False(project.IsPartiallyRunning);
    }

    [Fact]
    public void RefreshRunningCounts_無服務_不亮燈()
    {
        var project = CreateProject("A", running: 0);

        Assert.False(project.IsFullyRunning);
        Assert.False(project.IsPartiallyRunning);
    }

    [Fact]
    public void BuildRunningProjectsSummary_單一git專案()
    {
        var project = CreateProject("CMPBackend-1859-reseller-202611041340", running: 1, branch: "feat/1859");

        Assert.Equal(
            "執行中：CMPBackend-1859-reseller-202611041340 (feat/1859)",
            MainViewModel.BuildRunningProjectsSummary([project]));
    }

    [Fact]
    public void BuildRunningProjectsSummary_多專案以頓號分隔且略過全停()
    {
        var a = CreateProject("A", running: 1, branch: "dev");
        var b = CreateProject("B", running: 0, stopped: 2);
        var c = CreateProject("C", running: 1, isGitRepo: false, branch: "");

        Assert.Equal("執行中：A (dev)、C", MainViewModel.BuildRunningProjectsSummary([a, b, c]));
    }

    [Fact]
    public void BuildRunningProjectsSummary_分支空白不帶括號()
    {
        var project = CreateProject("A", running: 1, branch: "");

        Assert.Equal("執行中：A", MainViewModel.BuildRunningProjectsSummary([project]));
    }

    [Fact]
    public void BuildRunningProjectsSummary_無執行中_回空字串()
    {
        var project = CreateProject("A", running: 0, stopped: 1);

        Assert.Equal("", MainViewModel.BuildRunningProjectsSummary([project]));
    }
}
