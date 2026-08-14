using System.IO;
using System.Reflection;
using DockerDashboard.Models;
using DockerDashboard.Services;
using Xunit;

namespace DockerDashboard.Tests;

public class ComposeFileScannerTests
{
    [Fact]
    public void ParseJsonConfig_BuildContext未指定Dockerfile時_推導預設Dockerfile路徑()
    {
        var root = Path.Combine(Path.GetTempPath(), "compose-scan-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            var scanner = new ComposeFileScanner(new ScanCacheService(Path.Combine(root, "cache.json")));
            var parseJsonConfig = typeof(ComposeFileScanner).GetMethod(
                "ParseJsonConfig",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var composePath = Path.Combine(root, "docker-compose.yml");
            var json = """
                {
                  "name": "demo",
                  "services": {
                    "api": {
                      "build": {
                        "context": "./src/Api"
                      }
                    }
                  }
                }
                """;

            var result = (ComposeFile?)parseJsonConfig.Invoke(scanner, [json, composePath, root]);

            var compose = Assert.IsType<ComposeFile>(result);
            var service = Assert.Single(compose.Services);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(root, "src", "Api", "Dockerfile")),
                service.DockerfilePath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ParseJsonConfig_Wsl2輸出Linux絕對路徑時_轉回Windows路徑()
    {
        var root = Path.Combine(Path.GetTempPath(), "compose-scan-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            var scanner = new ComposeFileScanner(new ScanCacheService(Path.Combine(root, "cache.json")))
            {
                DockerMode = DockerMode.Wsl2,
                WslDistroName = "Ubuntu"
            };
            var parseJsonConfig = typeof(ComposeFileScanner).GetMethod(
                "ParseJsonConfig",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var composePath = Path.Combine(root, "docker-compose.yml");
            var json = """
                {
                  "name": "demo",
                  "services": {
                    "api": {
                      "build": {
                        "context": "/mnt/c/work/demo/src/Api"
                      }
                    }
                  }
                }
                """;

            var result = (ComposeFile?)parseJsonConfig.Invoke(scanner, [json, composePath, root]);

            var compose = Assert.IsType<ComposeFile>(result);
            var service = Assert.Single(compose.Services);
            Assert.Equal(@"C:\work\demo\src\Api\Dockerfile", service.DockerfilePath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
