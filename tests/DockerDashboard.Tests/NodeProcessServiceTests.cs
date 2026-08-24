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

    // 回歸測試：ArgumentList 會用 CRT 規則跳脫，把使用者指令裡的引號改成 \"（cmd 不認得），
    // E2E 常見的 --grep "xxx" 會因此壞掉。改用 /s /c 傳原始命令列後，引號應原樣傳給 cmd。
    [Fact]
    public async Task Start_指令含雙引號_不被額外跳脫()
    {
        var service = new NodeProcessService();

        using var stream = service.Start(Path.GetTempPath(), "echo \"a b\"");
        var output = await stream.StandardOutput.ReadToEndAsync();
        await stream.WaitForExitAsync();

        Assert.DoesNotContain("\\\"", output);
        Assert.Contains("\"a b\"", output);
    }
}
