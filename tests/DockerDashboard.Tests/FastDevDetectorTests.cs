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
    public void Detect_單一符合服務名的csproj_自動採用並回相對POSIX路徑()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "OrderBackend"));
            File.WriteAllText(Path.Combine(root, "OrderBackend", "OrderBackend.csproj"), "<Project/>");

            var result = FastDevDetector.Detect(root, "orderbackend", "sdk:default");

            Assert.Single(result.CsprojCandidates);
            Assert.Equal("OrderBackend/OrderBackend.csproj", result.CsprojCandidates[0]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Detect_讀Dockerfile的sdk版本()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "OrderBackend"));
            File.WriteAllText(Path.Combine(root, "OrderBackend", "OrderBackend.csproj"), "<Project/>");
            File.WriteAllText(Path.Combine(root, "OrderBackend", "Dockerfile"),
                "FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base\nFROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\n");

            var result = FastDevDetector.Detect(root, "orderbackend", "sdk:default");

            Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", result.SdkImage);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Detect_無Dockerfile_用預設SDK()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "OrderBackend"));
            File.WriteAllText(Path.Combine(root, "OrderBackend", "OrderBackend.csproj"), "<Project/>");

            var result = FastDevDetector.Detect(root, "orderbackend", "sdk:default");

            Assert.Equal("sdk:default", result.SdkImage);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Detect_多個csproj且無一符合服務名_全列為候選()
    {
        var root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Foo"));
            Directory.CreateDirectory(Path.Combine(root, "Bar"));
            File.WriteAllText(Path.Combine(root, "Foo", "Foo.csproj"), "<Project/>");
            File.WriteAllText(Path.Combine(root, "Bar", "Bar.csproj"), "<Project/>");

            var result = FastDevDetector.Detect(root, "orderbackend", "sdk:default");

            Assert.Equal(2, result.CsprojCandidates.Count);
        }
        finally { Directory.Delete(root, true); }
    }
}
