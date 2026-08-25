using DockerDashboard.Models;

namespace DockerDashboard.Tests;

public class FrontendProjectTests
{
    [Fact]
    public void ToConfig_FromConfig_往返保留所有欄位()
    {
        var project = new FrontendProject
        {
            Name = "Meso",
            FolderPath = @"D:\repo\Meso",
            Group = FrontendGroup.External,
            DevCommand = "npm run dev -- --port 3002",
            InstallCommand = "npm ci",
            TestCommand = "npx vitest run",
            E2eCommand = "npx playwright test --ui",
        };

        var restored = FrontendProject.FromConfig(project.ToConfig());

        Assert.Equal("Meso", restored.Name);
        Assert.Equal(@"D:\repo\Meso", restored.FolderPath);
        Assert.Equal(FrontendGroup.External, restored.Group);
        Assert.Equal("npm run dev -- --port 3002", restored.DevCommand);
        Assert.Equal("npm ci", restored.InstallCommand);
        Assert.Equal("npx vitest run", restored.TestCommand);
        Assert.Equal("npx playwright test --ui", restored.E2eCommand);
    }

    [Fact]
    public void 新專案_預設指令與狀態()
    {
        var project = new FrontendProject();

        Assert.Equal("npm run dev", project.DevCommand);
        Assert.Equal("npm install", project.InstallCommand);
        Assert.Equal("npm run test", project.TestCommand);
        Assert.Equal("npx playwright test", project.E2eCommand);
        Assert.Equal(FrontendStatus.Stopped, project.Status);
        Assert.False(project.IsDevRunning);
    }

    [Fact]
    public void FolderName_取路徑最後一段()
    {
        var project = new FrontendProject { FolderPath = @"D:\repo\Meso-feat-x\" };
        Assert.Equal("Meso-feat-x", project.FolderName);
    }

    // 邊界測試：磁碟根目錄 GetFileName 回傳空字串，FolderName 不該顯示空白
    [Fact]
    public void FolderName_磁碟根目錄時回退顯示完整路徑()
    {
        var project = new FrontendProject { FolderPath = @"D:\" };
        Assert.Equal(@"D:\", project.FolderName);
    }

    // 回歸測試：dev server 崩潰時，一次性指令（install/vitest/e2e）仍在跑，Status 不該被一次性指令的收尾覆寫
    [Fact]
    public void Status_不受一次性指令收尾影響_dev崩潰後仍維持Crashed()
    {
        var project = new FrontendProject { IsOneShotRunning = true };

        project.Status = FrontendStatus.Crashed; // 模擬 RunDevProcessAsync 崩潰分支
        project.IsOneShotRunning = false; // 模擬 RunOneShotAsync 完成收尾，不該動到 Status

        Assert.Equal(FrontendStatus.Crashed, project.Status);
    }

    // 回歸測試：一次性指令執行中時取消鈕（綁 IsBusy）不該因 dev server 崩潰而消失
    [Fact]
    public void IsBusy_不受Status變化影響_只看IsOneShotRunning()
    {
        var project = new FrontendProject { IsOneShotRunning = true };

        project.Status = FrontendStatus.Crashed; // 模擬 dev server 同時崩潰

        Assert.True(project.IsBusy);
        Assert.False(project.IsNotBusy);
    }

    [Fact]
    public void DisplayStatus_一次性指令執行中時顯示Busy_即使Status是其他值()
    {
        var project = new FrontendProject { Status = FrontendStatus.Stopped, IsOneShotRunning = true };
        Assert.Equal(FrontendStatus.Busy, project.DisplayStatus);
    }

    [Fact]
    public void DisplayStatus_無一次性指令時直接反映Status()
    {
        var project = new FrontendProject { Status = FrontendStatus.Crashed };
        Assert.Equal(FrontendStatus.Crashed, project.DisplayStatus);
    }

    // IsIdle 供「編輯」按鈕的 IsEnabled 使用：有任何行程在跑就不可編輯
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void IsIdle_任一行程執行中即為false(bool devRunning, bool oneShotRunning, bool expected)
    {
        var project = new FrontendProject
        {
            IsDevRunning = devRunning,
            IsOneShotRunning = oneShotRunning,
        };

        Assert.Equal(expected, project.IsIdle);
    }

    // 啟動流程的 await 空窗期（互斥確認、讀分支）IsDevRunning 尚未 true，
    // 若不算入 IsIdle，使用者可在這段期間改組別造成互斥與 log 歸屬錯亂
    [Fact]
    public void IsIdle_啟動途中為false()
    {
        var project = new FrontendProject { IsStarting = true };

        Assert.False(project.IsIdle);
    }
}
