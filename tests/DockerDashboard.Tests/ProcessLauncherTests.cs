using System.Diagnostics;
using DockerDashboard.Services;

namespace DockerDashboard.Tests;

public class ProcessLauncherTests
{
    private static ProcessStartInfo Psi(string command) => new()
    {
        FileName = "cmd.exe",
        Arguments = $"/c {command}",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        CreateNoWindow = true,
    };

    [Fact]
    public void Start_啟動的行程被納入App的Job()
    {
        using var process = ProcessLauncher.Start(Psi("ping -n 10 127.0.0.1"));
        try
        {
            Assert.True(ProcessLauncher.IsInAppJob(process));
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    [Fact]
    public void StartDetached_啟動的行程不在App的Job()
    {
        var psi = Psi("ping -n 10 127.0.0.1");
        var process = ProcessLauncher.StartDetached(psi);
        Assert.NotNull(process);
        try
        {
            Assert.False(ProcessLauncher.IsInAppJob(process!));
        }
        finally
        {
            try { process!.Kill(entireProcessTree: true); } catch { }
            process!.Dispose();
        }
    }

    // 行程可能在 AssignProcessToJobObject 之前就結束，指派會失敗；此時仍須正常回傳
    [Fact]
    public void Start_行程立即結束也不拋例外()
    {
        using var process = ProcessLauncher.Start(Psi("exit 0"));
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
