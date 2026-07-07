using System.IO;
using DockerDashboard.Models;
using DockerDashboard.Services;

namespace DockerDashboard.Tests;

public sealed class ScanCacheServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ScanCacheServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scancache-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static ComposeFile MakeComposeFile(string dir)
    {
        var compose = new ComposeFile
        {
            FileName = "docker-compose.yml",
            FilePath = Path.Combine(dir, "docker-compose.yml"),
            DirectoryPath = dir
        };
        compose.Services.Add(new DockerService
        {
            Name = "web",
            Image = "nginx",
            ContainerName = "my-web",
            Ports = "8080:80",
            ComposeFilePath = compose.FilePath,
            WorkingDirectory = dir
        });
        return compose;
    }

    private List<CachedFileStamp> WriteComposeAndStamp()
    {
        var path = Path.Combine(_tempDir, "docker-compose.yml");
        File.WriteAllText(path, "services:\n  web:\n    image: nginx\n");
        return ScanCacheService.GetStamps(_tempDir);
    }

    [Fact]
    public void TryGet_ReturnsNull_WhenNoEntry()
    {
        var cache = new ScanCacheService(Path.Combine(_tempDir, "cache.json"));
        var stamps = WriteComposeAndStamp();

        Assert.Null(cache.TryGet(_tempDir, stamps));
    }

    [Fact]
    public void TryGet_ReturnsComposeFile_WhenStampsMatch()
    {
        var cache = new ScanCacheService(Path.Combine(_tempDir, "cache.json"));
        var stamps = WriteComposeAndStamp();
        cache.Store(_tempDir, stamps, MakeComposeFile(_tempDir));

        var hit = cache.TryGet(_tempDir, stamps);

        Assert.NotNull(hit);
        var svc = Assert.Single(hit.Services);
        Assert.Equal("web", svc.Name);
        Assert.Equal("my-web", svc.ContainerName);
        Assert.Equal("8080:80", svc.Ports);
        Assert.Equal(_tempDir, svc.WorkingDirectory);
    }

    [Fact]
    public void TryGet_ReturnsNull_WhenFileChanged()
    {
        var cache = new ScanCacheService(Path.Combine(_tempDir, "cache.json"));
        var stamps = WriteComposeAndStamp();
        cache.Store(_tempDir, stamps, MakeComposeFile(_tempDir));

        var changed = stamps
            .Select(s => s with { MTimeUtcTicks = s.MTimeUtcTicks + 1 })
            .ToList();

        Assert.Null(cache.TryGet(_tempDir, changed));
    }

    [Fact]
    public void TryGet_ReturnsNull_WhenFileSetDiffers()
    {
        var cache = new ScanCacheService(Path.Combine(_tempDir, "cache.json"));
        var stamps = WriteComposeAndStamp();
        cache.Store(_tempDir, stamps, MakeComposeFile(_tempDir));

        var extra = new List<CachedFileStamp>(stamps)
        {
            new(Path.Combine(_tempDir, "docker-compose.override.yml"), 123)
        };

        Assert.Null(cache.TryGet(_tempDir, extra));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var cachePath = Path.Combine(_tempDir, "cache.json");
        var stamps = WriteComposeAndStamp();

        var writer = new ScanCacheService(cachePath);
        writer.Store(_tempDir, stamps, MakeComposeFile(_tempDir));
        await writer.SaveAsync();

        var reader = new ScanCacheService(cachePath);
        await reader.LoadAsync();

        Assert.NotNull(reader.TryGet(_tempDir, stamps));
    }

    [Fact]
    public void GetStamps_ReturnsMainAndOverrideFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "docker-compose.yml"), "services: {}");
        File.WriteAllText(Path.Combine(_tempDir, "docker-compose.override.yml"), "services: {}");

        var stamps = ScanCacheService.GetStamps(_tempDir);

        Assert.Equal(2, stamps.Count);
        Assert.All(stamps, s => Assert.True(s.MTimeUtcTicks > 0));
    }
}
