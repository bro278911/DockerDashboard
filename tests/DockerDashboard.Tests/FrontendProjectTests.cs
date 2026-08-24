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
}
