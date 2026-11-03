using System.IO;
using System.Reflection;
using System.Text;
using DockerDashboard.Models;
using DockerDashboard.Services;
using Xunit;

namespace DockerDashboard.Tests;

public class ComposeFileScannerTests
{
    [Fact]
    public void CreateDockerComposeProcessStartInfo_以Utf8讀取輸出與錯誤()
    {
        var root = Path.Combine(Path.GetTempPath(), "compose-scan-桌面內容");

        var psi = ComposeFileScanner.CreateDockerComposeProcessStartInfo(
            root,
            DockerMode.DockerDesktop,
            "Ubuntu");

        Assert.Equal(Encoding.UTF8, psi.StandardOutputEncoding);
        Assert.Equal(Encoding.UTF8, psi.StandardErrorEncoding);
    }

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

    [Fact]
    public void ParseJsonConfig_讀取depends_on物件的key()
    {
        var root = Path.Combine(Path.GetTempPath(), "compose-scan-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            var scanner = new ComposeFileScanner(new ScanCacheService(Path.Combine(root, "cache.json")));
            var parseJsonConfig = typeof(ComposeFileScanner).GetMethod(
                "ParseJsonConfig",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var json = """
                {
                  "name": "demo",
                  "services": {
                    "nginx": {
                      "depends_on": {
                        "api": { "condition": "service_started", "required": true },
                        "web": { "condition": "service_started", "required": true }
                      }
                    },
                    "api": {}
                  }
                }
                """;

            var compose = Assert.IsType<ComposeFile>(
                parseJsonConfig.Invoke(scanner, [json, Path.Combine(root, "docker-compose.yml"), root]));

            Assert.Equal(["api", "web"], compose.Services.Single(s => s.Name == "nginx").DependsOn);
            Assert.Empty(compose.Services.Single(s => s.Name == "api").DependsOn);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ParseManually_depends_on支援清單與map兩種寫法()
    {
        var root = Path.Combine(Path.GetTempPath(), "compose-scan-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            var scanner = new ComposeFileScanner(new ScanCacheService(Path.Combine(root, "cache.json")));
            var parseManually = typeof(ComposeFileScanner).GetMethod(
                "ParseManually",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var path = Path.Combine(root, "docker-compose.yml");
            File.WriteAllText(path, """
                services:
                  nginx:
                    image: nginx
                    depends_on:
                      - api
                      - web
                  gateway:
                    image: gw
                    depends_on:
                      api:
                        condition: service_started
                  api:
                    image: api
                """);

            var compose = Assert.IsType<ComposeFile>(parseManually.Invoke(scanner, [path]));

            Assert.Equal(["api", "web"], compose.Services.Single(s => s.Name == "nginx").DependsOn);
            Assert.Equal(["api"], compose.Services.Single(s => s.Name == "gateway").DependsOn);
            Assert.Empty(compose.Services.Single(s => s.Name == "api").DependsOn);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
