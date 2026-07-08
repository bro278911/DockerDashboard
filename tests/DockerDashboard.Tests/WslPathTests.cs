using System.IO;
using DockerDashboard.Services;
using Xunit;

namespace DockerDashboard.Tests;

public class WslPathTests
{
    [Theory]
    [InlineData(@"D:\projects\app", "/mnt/d/projects/app")]
    [InlineData(@"C:/projects/app", "/mnt/c/projects/app")]
    [InlineData(@"\\wsl$\Ubuntu\home\user\app", "/home/user/app")]
    [InlineData(@"\\wsl.localhost\Ubuntu-22.04\home\user\app", "/home/user/app")]
    [InlineData(@"\\WSL$\Ubuntu\home\user", "/home/user")]
    [InlineData(@"\\wsl$\Ubuntu\home\user\", "/home/user/")]
    [InlineData(@"\\wsl$\Ubuntu", "/")]
    [InlineData(@"\\server\share\folder", @"\\server\share\folder")]
    [InlineData("", "")]
    public void ConvertToWslPath_轉換各種路徑(string input, string expected)
    {
        Assert.Equal(expected, DockerCliService.ConvertToWslPath(input));
    }

    [Theory]
    [InlineData(@"\\wsl$\Ubuntu\home\user\app", true)]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\user\app", true)]
    [InlineData(@"\\WSL.LOCALHOST\Ubuntu\home", true)]
    [InlineData(@"D:\projects\app", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData("", false)]
    public void IsWslUncPath_判斷是否為WSL路徑(string input, bool expected)
    {
        Assert.Equal(expected, DockerCliService.IsWslUncPath(input));
    }
}
