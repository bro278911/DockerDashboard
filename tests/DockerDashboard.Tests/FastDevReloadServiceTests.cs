using System;
using System.Threading;
using System.Threading.Tasks;
using DockerDashboard.Services;
using Xunit;

namespace DockerDashboard.Tests;

public class FastDevReloadServiceTests
{
    [Fact]
    public async Task TriggerDebounce_執行中再觸發_不重疊且補跑一次()
    {
        using var svc = new FastDevReloadService { DebounceDelay = TimeSpan.FromMilliseconds(30) };
        var running = 0;
        var maxConcurrent = 0;
        var runs = 0;
        svc.OnSolutionChanged = async _ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxConcurrent, now);
            Interlocked.Increment(ref runs);
            await Task.Delay(200);
            Interlocked.Decrement(ref running);
        };

        svc.TriggerDebounce("dir");
        await Task.Delay(100);
        svc.TriggerDebounce("dir");
        await Task.Delay(700);

        Assert.Equal(1, maxConcurrent);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task TriggerDebounce_debounce期間連續觸發_只跑一次()
    {
        using var svc = new FastDevReloadService { DebounceDelay = TimeSpan.FromMilliseconds(80) };
        var runs = 0;
        svc.OnSolutionChanged = _ =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        };

        svc.TriggerDebounce("dir");
        svc.TriggerDebounce("dir");
        svc.TriggerDebounce("dir");
        await Task.Delay(400);

        Assert.Equal(1, runs);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int cur;
        while (value > (cur = Volatile.Read(ref target)) &&
               Interlocked.CompareExchange(ref target, value, cur) != cur) { }
    }
}
