using System.IO;
using DockerDashboard.Services;
using Xunit;

namespace DockerDashboard.Tests;

public class FastDevOverrideStoreTests
{
    [Fact]
    public void Write再Delete_檔案建立後可移除()
    {
        var key = "unit-test-key::svc" + Path.GetRandomFileName();
        var path = FastDevOverrideStore.Write(key, "services: {}\n");
        try
        {
            Assert.True(File.Exists(path));
            Assert.Equal("services: {}\n", File.ReadAllText(path));
        }
        finally
        {
            FastDevOverrideStore.Delete(key);
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public void PathFor_相同key回相同路徑()
    {
        var key = "same::key";
        Assert.Equal(FastDevOverrideStore.PathFor(key), FastDevOverrideStore.PathFor(key));
    }
}
