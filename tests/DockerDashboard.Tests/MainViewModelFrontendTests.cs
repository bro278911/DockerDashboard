using DockerDashboard.Models;
using DockerDashboard.ViewModels;

namespace DockerDashboard.Tests;

public class MainViewModelFrontendTests
{
    [Fact]
    public void FindRunningInGroup_回傳同組另一個執行中的專案()
    {
        var running = new FrontendProject { Name = "A", IsDevRunning = true };
        var candidate = new FrontendProject { Name = "B" };

        var found = MainViewModel.FindRunningInGroup([running, candidate], candidate);

        Assert.Same(running, found);
    }

    [Fact]
    public void FindRunningInGroup_排除自己且無其他執行中時回傳null()
    {
        var candidate = new FrontendProject { Name = "A", IsDevRunning = true };

        Assert.Null(MainViewModel.FindRunningInGroup([candidate], candidate));
    }

    [Fact]
    public void BuildRunningLabel_顯示名稱資料夾與分支()
    {
        var p = new FrontendProject
        {
            Name = "Meso",
            FolderPath = @"D:\repo\Meso-feat-x",
            CurrentBranch = "feat/x",
            IsDevRunning = true,
        };

        Assert.Equal("Meso（Meso-feat-x / feat/x）", MainViewModel.BuildRunningLabel([p]));
    }

    [Fact]
    public void BuildRunningLabel_非git資料夾顯示替代字樣()
    {
        var p = new FrontendProject { Name = "A", FolderPath = @"D:\x\A", IsDevRunning = true };

        Assert.Equal("A（A / 非 git）", MainViewModel.BuildRunningLabel([p]));
    }

    [Fact]
    public void BuildRunningLabel_無執行中專案()
    {
        Assert.Equal("無執行中專案", MainViewModel.BuildRunningLabel([new FrontendProject()]));
    }
}
