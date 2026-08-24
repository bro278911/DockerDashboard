using System.IO;
using DockerDashboard.Services;

namespace DockerDashboard.Tests;

public class NodeProcessServiceTests
{
    [Fact]
    public async Task Start_執行echo_可讀輸出且exitcode為0()
    {
        var service = new NodeProcessService();

        using var stream = service.Start(Path.GetTempPath(), "echo hello-frontend");
        var output = await stream.StandardOutput.ReadToEndAsync();
        await stream.WaitForExitAsync();

        Assert.Contains("hello-frontend", output);
        Assert.Equal(0, stream.ExitCode);
    }

    [Fact]
    public async Task Start_指令失敗_exitcode非0()
    {
        var service = new NodeProcessService();

        using var stream = service.Start(Path.GetTempPath(), "exit 3");
        await stream.WaitForExitAsync();

        Assert.Equal(3, stream.ExitCode);
    }
}
