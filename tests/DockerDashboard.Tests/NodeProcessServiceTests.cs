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

    // 回歸測試：ProcessStream.Dispose 必須真的終止行程。
    // 若 _disposed 又被放回 Kill 之前，Kill 開頭的檢查會直接 return，
    // 前端 dev server 與後端 docker logs -f 都會殘留，而其他測試都驗不到這件事
    [Fact]
    public async Task Dispose_應終止仍在執行的行程()
    {
        var service = new NodeProcessService();
        var stream = service.Start(Path.GetTempPath(), "ping -n 30 127.0.0.1");

        var pid = stream.Id;
        Assert.NotNull(pid);

        stream.Dispose();

        // 給 OS 一點回收時間，輪詢確認行程真的不見了
        var exited = false;
        for (var i = 0; i < 25 && !exited; i++)
        {
            try
            {
                using var probe = System.Diagnostics.Process.GetProcessById(pid!.Value);
                exited = probe.HasExited;
            }
            catch (ArgumentException)
            {
                exited = true; // 行程已不存在
            }

            if (!exited) await Task.Delay(200);
        }

        Assert.True(exited, "Dispose 後行程仍在執行");
    }
}
