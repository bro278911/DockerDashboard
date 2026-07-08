using DockerDashboard.Services;
using Xunit;

namespace DockerDashboard.Tests;

public class BuildEnvTests
{
    [Fact]
    public void GetBuildEnv_永遠包含COMPOSE_BAKE()
    {
        var env = DockerCliService.GetBuildEnv(0);
        Assert.Equal("true", env["COMPOSE_BAKE"]);
        Assert.False(env.ContainsKey("BUILDKIT_MAX_PARALLELISM"));
    }

    [Fact]
    public void GetBuildEnv_平行度大於零時包含BUILDKIT_MAX_PARALLELISM()
    {
        var env = DockerCliService.GetBuildEnv(4);
        Assert.Equal("true", env["COMPOSE_BAKE"]);
        Assert.Equal("4", env["BUILDKIT_MAX_PARALLELISM"]);
    }
}
