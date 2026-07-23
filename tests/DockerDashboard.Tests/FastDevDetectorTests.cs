using System.IO;
using DockerDashboard.Services;
using Xunit;

namespace DockerDashboard.Tests;

public class FastDevDetectorTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fdtest_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Detect_單一符合服務名的csproj_自動採用()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "OrderBackend"));
            File.WriteAllText(Path.Combine(root, "OrderBackend", "OrderBackend.csproj"), "<Project/>");
            var result = FastDevDetector.Detect(root, "orderbackend", "aspnet:default");
            Assert.Single(result.CsprojCandidates);
            Assert.Equal("OrderBackend/OrderBackend.csproj", result.CsprojCandidates[0]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Detect_讀Dockerfile的aspnet版本與solution()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "OrderBackend"));
            File.WriteAllText(Path.Combine(root, "OrderBackend", "OrderBackend.csproj"), "<Project/>");
            File.WriteAllText(Path.Combine(root, "OrderBackend", "Dockerfile"),
                "FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base\nFROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\n");
            File.WriteAllText(Path.Combine(root, "CMPBackend.sln"), "Microsoft Visual Studio Solution File");
            var result = FastDevDetector.Detect(root, "orderbackend", "aspnet:default");
            Assert.Equal("mcr.microsoft.com/dotnet/aspnet:10.0", result.RuntimeImage);
            Assert.EndsWith("CMPBackend.sln", result.SolutionPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Detect_無Dockerfile_用預設runtime且solution為null()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "OrderBackend"));
            File.WriteAllText(Path.Combine(root, "OrderBackend", "OrderBackend.csproj"), "<Project/>");
            var result = FastDevDetector.Detect(root, "orderbackend", "aspnet:default");
            Assert.Equal("aspnet:default", result.RuntimeImage);
            Assert.Null(result.SolutionPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ReadProjectInfo_讀TFM與AssemblyName()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "OrderBackend"));
            File.WriteAllText(Path.Combine(root, "OrderBackend", "OrderBackend.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>OrderApi</AssemblyName></PropertyGroup></Project>");
            var info = FastDevDetector.ReadProjectInfo(root, "OrderBackend/OrderBackend.csproj", "net10.0");
            Assert.Equal("net10.0", info.Tfm);
            Assert.Equal("OrderApi", info.AssemblyName);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ReadProjectInfo_無AssemblyName時用csproj檔名()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "OrderBackend"));
            File.WriteAllText(Path.Combine(root, "OrderBackend", "OrderBackend.csproj"),
                "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            var info = FastDevDetector.ReadProjectInfo(root, "OrderBackend/OrderBackend.csproj", "net9.0");
            Assert.Equal("net10.0", info.Tfm);
            Assert.Equal("OrderBackend", info.AssemblyName);
        }
        finally { Directory.Delete(root, true); }
    }
}
