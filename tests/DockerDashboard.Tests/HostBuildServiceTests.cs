using System.Collections.Generic;
using DockerDashboard.Services;
using Xunit;

namespace DockerDashboard.Tests;

public class HostBuildServiceTests
{
    [Fact]
    public void ChangedServiceKeys_只回mtime變新的()
    {
        var before = new Dictionary<string, (string DllPath, long BeforeTicks)>
        {
            ["svcA"] = ("A.dll", 100),
            ["svcB"] = ("B.dll", 200),
            ["svcC"] = ("C.dll", 300),
        };
        long Current(string dll) => dll switch
        {
            "A.dll" => 150,
            "B.dll" => 200,
            "C.dll" => 500,
            _ => 0
        };

        var changed = HostBuildService.ChangedServiceKeys(before, Current);

        Assert.Contains("svcA", changed);
        Assert.Contains("svcC", changed);
        Assert.DoesNotContain("svcB", changed);
    }

    [Fact]
    public void ChangedServiceKeys_全沒變回空()
    {
        var before = new Dictionary<string, (string DllPath, long BeforeTicks)>
        {
            ["svcA"] = ("A.dll", 100),
        };
        var changed = HostBuildService.ChangedServiceKeys(before, _ => 100);
        Assert.Empty(changed);
    }
}
