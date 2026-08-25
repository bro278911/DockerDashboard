using System.Collections.ObjectModel;
using DockerDashboard.Models;
using DockerDashboard.ViewModels;

namespace DockerDashboard.Tests;

public class MainViewModelFrontendTests
{
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

    [Fact]
    public void ApplyFrontendEdit_套用名稱與四個指令()
    {
        var project = new FrontendProject
        {
            Name = "舊名",
            FolderPath = @"D:\repo\Meso",
            Group = FrontendGroup.Internal,
        };
        var internalProjects = new ObservableCollection<FrontendProject> { project };
        var externalProjects = new ObservableCollection<FrontendProject>();

        var edited = new FrontendProjectConfig
        {
            Name = "新名",
            FolderPath = @"D:\repo\Meso",
            Group = FrontendGroup.Internal,
            DevCommand = "pnpm dev",
            InstallCommand = "pnpm install",
            TestCommand = "pnpm test",
            E2eCommand = "pnpm e2e",
        };

        MainViewModel.ApplyFrontendEdit(project, edited, internalProjects, externalProjects);

        Assert.Equal("新名", project.Name);
        Assert.Equal("pnpm dev", project.DevCommand);
        Assert.Equal("pnpm install", project.InstallCommand);
        Assert.Equal("pnpm test", project.TestCommand);
        Assert.Equal("pnpm e2e", project.E2eCommand);
        // 資料夾路徑不開放編輯，維持原值
        Assert.Equal(@"D:\repo\Meso", project.FolderPath);
    }

    [Fact]
    public void ApplyFrontendEdit_組別改變時在兩個集合間搬移()
    {
        var project = new FrontendProject { Name = "A", Group = FrontendGroup.Internal };
        var internalProjects = new ObservableCollection<FrontendProject> { project };
        var externalProjects = new ObservableCollection<FrontendProject>();

        var edited = project.ToConfig();
        edited.Group = FrontendGroup.External;

        MainViewModel.ApplyFrontendEdit(project, edited, internalProjects, externalProjects);

        Assert.Equal(FrontendGroup.External, project.Group);
        Assert.Empty(internalProjects);
        Assert.Same(project, Assert.Single(externalProjects));
    }

    [Fact]
    public void ApplyFrontendEdit_組別未變時集合不動()
    {
        var project = new FrontendProject { Name = "A", Group = FrontendGroup.External };
        var other = new FrontendProject { Name = "B", Group = FrontendGroup.External };
        var internalProjects = new ObservableCollection<FrontendProject>();
        var externalProjects = new ObservableCollection<FrontendProject> { project, other };

        var edited = project.ToConfig();
        edited.Name = "A2";

        MainViewModel.ApplyFrontendEdit(project, edited, internalProjects, externalProjects);

        Assert.Equal("A2", project.Name);
        Assert.Empty(internalProjects);
        Assert.Equal(2, externalProjects.Count);
        Assert.Same(project, externalProjects[0]); // 順序未被搬移打亂
    }
}
