using System.IO;
using DockerDashboard.Models;
using DockerDashboard.Services;

namespace DockerDashboard.Tests;

public class FrontendProcessManagerTests
{
    private static FrontendProcessManager CreateManager() => new(new NodeProcessService());

    private static FrontendProject Project(string devCommand) => new()
    {
        Name = "test",
        FolderPath = Path.GetTempPath(),
        DevCommand = devCommand,
    };

    private static async Task<FrontendStateEventArgs> WaitForStateAsync(
        FrontendProcessManager manager, FrontendProcessState expected, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<FrontendStateEventArgs>();
        void Handler(object? _, FrontendStateEventArgs e)
        {
            if (e.State == expected) tcs.TrySetResult(e);
        }

        manager.StateChanged += Handler;
        try
        {
            return await tcs.Task.WaitAsync(timeout);
        }
        finally
        {
            manager.StateChanged -= Handler;
        }
    }

    [Fact]
    public async Task StartDevAsync_行程自行結束_回報Crashed並清空追蹤()
    {
        var manager = CreateManager();
        var project = Project("exit 0");

        var crashed = WaitForStateAsync(manager, FrontendProcessState.Crashed, TimeSpan.FromSeconds(20));
        var result = await manager.StartDevAsync(project, [project]);

        Assert.IsType<StartResult.Started>(result);
        var evt = await crashed;
        Assert.Equal(0, evt.ExitCode);
        Assert.False(manager.IsDevActive(project));
    }

    [Fact]
    public async Task StartDevAsync_行程非零結束_帶出exitcode()
    {
        var manager = CreateManager();
        var project = Project("exit 3");

        var crashed = WaitForStateAsync(manager, FrontendProcessState.Crashed, TimeSpan.FromSeconds(20));
        await manager.StartDevAsync(project, [project]);

        var evt = await crashed;
        Assert.Equal(3, evt.ExitCode);
    }

    [Fact]
    public async Task StopDevAsync_使用者主動停止_回報Stopped而非Crashed()
    {
        var manager = CreateManager();
        var project = Project("ping -n 60 127.0.0.1");

        await manager.StartDevAsync(project, [project]);

        var stopped = WaitForStateAsync(manager, FrontendProcessState.Stopped, TimeSpan.FromSeconds(20));
        Assert.True(await manager.StopDevAsync(project));

        var evt = await stopped;
        Assert.Equal(FrontendProcessState.Stopped, evt.State);
    }

    [Fact]
    public async Task StopDevAsync_長駐行程_確實停止並回傳true()
    {
        var manager = CreateManager();
        var project = Project("ping -n 60 127.0.0.1");

        await manager.StartDevAsync(project, [project]);
        Assert.True(manager.IsDevActive(project));

        Assert.True(await manager.StopDevAsync(project));
        Assert.False(manager.IsDevActive(project));
    }

    [Fact]
    public async Task StopDevAsync_重複呼叫_冪等且仍回傳true()
    {
        var manager = CreateManager();
        var project = Project("ping -n 60 127.0.0.1");

        await manager.StartDevAsync(project, [project]);
        Assert.True(await manager.StopDevAsync(project));
        Assert.True(await manager.StopDevAsync(project));
    }

    [Fact]
    public async Task StartDevAsync_同組已有執行中_回報GroupOccupied且不啟動()
    {
        var manager = CreateManager();
        var running = Project("ping -n 60 127.0.0.1");
        var candidate = Project("ping -n 60 127.0.0.1");

        await manager.StartDevAsync(running, [running, candidate]);

        var result = await manager.StartDevAsync(candidate, [running, candidate]);

        var occupied = Assert.IsType<StartResult.GroupOccupied>(result);
        Assert.Same(running, occupied.Occupant);
        Assert.False(manager.IsDevActive(candidate));

        await manager.StopDevAsync(running);
    }

    [Fact]
    public async Task StartDevAsync_同專案重複啟動_回報AlreadyRunning()
    {
        var manager = CreateManager();
        var project = Project("ping -n 60 127.0.0.1");

        await manager.StartDevAsync(project, [project]);
        var result = await manager.StartDevAsync(project, [project]);

        Assert.IsType<StartResult.AlreadyRunning>(result);
        await manager.StopDevAsync(project);
    }

    [Fact]
    public async Task OutputReceived_帶出行程輸出()
    {
        var manager = CreateManager();
        var project = Project("echo hello-manager");

        var tcs = new TaskCompletionSource<string>();
        manager.OutputReceived += (_, e) =>
        {
            if (e.Line.Contains("hello-manager")) tcs.TrySetResult(e.Line);
        };

        await manager.StartDevAsync(project, [project]);

        Assert.Contains("hello-manager", await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20)));
    }

    // 釘住：StateChanged 訂閱者拋例外時，MonitorAsync 尾端仍必須釋放資源（Stream/Cts Dispose）並
    // 清空追蹤字典，例外本身不能讓監控流程中斷。StartDevAsync 內部也會同步觸發一次 Running 事件
    // （見 StartDevAsync 實作），為了單純測試 MonitorAsync 尾端這條路徑，拋例外的處理常式要在
    // StartDevAsync 回傳（Running 事件已同步觸發完畢）之後才訂閱，避免連帶炸到 StartDevAsync 本身。
    [Fact]
    public async Task StateChanged_訂閱者拋例外_資源仍釋放且字典仍清空()
    {
        var manager = CreateManager();
        var project = Project("exit 0");

        var tcs = new TaskCompletionSource<FrontendStateEventArgs>();
        manager.StateChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Project, project) && e.State != FrontendProcessState.Running)
                tcs.TrySetResult(e);
        };

        var result = await manager.StartDevAsync(project, [project]);
        Assert.IsType<StartResult.Started>(result);

        manager.StateChanged += (_, _) => throw new InvalidOperationException("訂閱者刻意拋出例外，驗證 manager 的資源釋放不受影響");

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(FrontendProcessState.Crashed, evt.State);
        Assert.False(manager.IsDevActive(project));

        // 字典已正確清空、manager 未被拋例外的訂閱者卡住，同一專案可以再次成功啟動
        var restart = await manager.StartDevAsync(project, [project]);
        Assert.IsType<StartResult.Started>(restart);
    }

    // 釘住：OutputReceived 訂閱者拋例外時，讀取迴圈不能因此中斷，行程仍須正常跑完並回報正確的
    // 最終狀態；與上面的 StateChanged_訂閱者拋例外 測試對稱（同一原則，見 MonitorAsync 內註解）
    [Fact]
    public async Task OutputReceived_訂閱者拋例外_行程仍正常跑完且狀態正確()
    {
        var manager = CreateManager();
        var project = Project("echo hello-manager & exit 3");

        manager.OutputReceived += (_, _) => throw new InvalidOperationException("訂閱者刻意拋出例外，驗證讀取迴圈不受影響");

        var crashed = WaitForStateAsync(manager, FrontendProcessState.Crashed, TimeSpan.FromSeconds(20));
        var result = await manager.StartDevAsync(project, [project]);

        Assert.IsType<StartResult.Started>(result);
        var evt = await crashed;
        Assert.Equal(3, evt.ExitCode);
        Assert.False(manager.IsDevActive(project));
    }

    [Fact]
    public async Task RunOneShotAsync_與devserver並行_兩軸互不影響()
    {
        var manager = CreateManager();
        var project = Project("ping -n 60 127.0.0.1");

        await manager.StartDevAsync(project, [project]);
        var result = await manager.RunOneShotAsync(project, "ping -n 60 127.0.0.1", "install");

        Assert.IsType<StartResult.Started>(result);
        Assert.True(manager.IsDevActive(project));
        Assert.True(manager.IsOneShotActive(project));

        Assert.True(await manager.CancelOneShotAsync(project));
        Assert.False(manager.IsOneShotActive(project));
        Assert.True(manager.IsDevActive(project)); // dev 不受一次性指令取消影響

        await manager.StopDevAsync(project);
    }

    // 釘住：一次性指令一律回報 Stopped，但被使用者取消時 UserStopped 必須為 true，
    // 讓 UI 能區分「使用者取消」與「自然結束」（否則取消會被誤顯示成失敗訊息）
    [Fact]
    public async Task CancelOneShotAsync_使用者主動取消_UserStopped為true()
    {
        var manager = CreateManager();
        var project = Project("ping -n 60 127.0.0.1");

        var stopped = WaitForStateAsync(manager, FrontendProcessState.Stopped, TimeSpan.FromSeconds(20));
        await manager.RunOneShotAsync(project, "ping -n 60 127.0.0.1", "install");

        Assert.True(await manager.CancelOneShotAsync(project));

        var evt = await stopped;
        Assert.True(evt.UserStopped);
    }

    [Fact]
    public async Task RunOneShotAsync_行程自然結束_UserStopped為false()
    {
        var manager = CreateManager();
        var project = Project("exit 0");

        var stopped = WaitForStateAsync(manager, FrontendProcessState.Stopped, TimeSpan.FromSeconds(20));
        await manager.RunOneShotAsync(project, "exit 0", "install");

        var evt = await stopped;
        Assert.False(evt.UserStopped);
    }

    [Fact]
    public async Task RunOneShotAsync_同專案已有一次性指令_回報AlreadyRunning()
    {
        var manager = CreateManager();
        var project = Project("exit 0");

        await manager.RunOneShotAsync(project, "ping -n 60 127.0.0.1", "install");
        var result = await manager.RunOneShotAsync(project, "ping -n 60 127.0.0.1", "vitest");

        Assert.IsType<StartResult.AlreadyRunning>(result);
        await manager.CancelOneShotAsync(project);
    }

    // 本測試只驗證 StopAll 自身的職責：取得追蹤快照、清空 _entries、對每個 entry 走過終止路徑、
    // 且事後 manager 沒被卡死（同專案可以重新 StartDevAsync）。StopAll 對每個 entry 做的正是
    // Stream.Dispose()，「Dispose 會真的終止仍在執行的行程」已由 NodeProcessServiceTests 的
    // Dispose_應終止仍在執行的行程 這條回歸測試在單元層釘住；「App 行程死亡時子孫行程連帶回收」
    // 則是 ProcessLauncher 的 Job Object 機制保證，已實機驗證過，兩者都不必在這裡重複驗證
    [Fact]
    public async Task StopAll_清空追蹤且事後可重新啟動()
    {
        var manager = CreateManager();
        var a = Project("ping -n 60 127.0.0.1");
        var b = Project("ping -n 60 127.0.0.1");

        await manager.StartDevAsync(a, [a]);
        await manager.RunOneShotAsync(b, "ping -n 60 127.0.0.1", "install");

        manager.StopAll();

        Assert.False(manager.IsDevActive(a));
        Assert.False(manager.IsOneShotActive(b));

        // manager 沒被 StopAll 弄壞：同一個專案能再次成功啟動
        var restart = await manager.StartDevAsync(a, [a]);
        Assert.IsType<StartResult.Started>(restart);
        await manager.StopDevAsync(a);
    }
}
