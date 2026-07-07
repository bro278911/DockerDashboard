using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DockerDashboard.Models;

namespace DockerDashboard.Services;

public sealed record CachedFileStamp(string Path, long MTimeUtcTicks);

public sealed record CachedServiceDto(string Name, string Image, string ContainerName, string Ports);

public sealed record CachedDirectoryDto(
    List<CachedFileStamp> Files,
    string FileName,
    string FilePath,
    List<CachedServiceDto> Services);

// 以 compose 檔 mtime 驗證的掃描結果快取；命中時可跳過 docker compose config 解析
public sealed class ScanCacheService
{
    private static readonly string DefaultCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DockerDashboard",
        "scan-cache.json");

    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, CachedDirectoryDto> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private bool _loaded;

    public ScanCacheService() : this(DefaultCachePath) { }

    public ScanCacheService(string cachePath) => _cachePath = cachePath;

    public static List<CachedFileStamp> GetStamps(string directory)
    {
        return ComposeFileHelper.GetComposeFilePaths(directory)
            .Select(p => new CachedFileStamp(p, File.GetLastWriteTimeUtc(p).Ticks))
            .ToList();
    }

    public ComposeFile? TryGet(string directory, List<CachedFileStamp> currentStamps)
    {
        if (!_entries.TryGetValue(directory, out var entry))
            return null;

        if (entry.Files.Count != currentStamps.Count)
            return null;

        var cached = entry.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var current = currentStamps.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < cached.Count; i++)
        {
            if (!string.Equals(cached[i].Path, current[i].Path, StringComparison.OrdinalIgnoreCase) ||
                cached[i].MTimeUtcTicks != current[i].MTimeUtcTicks)
                return null;
        }

        var compose = new ComposeFile
        {
            FileName = entry.FileName,
            FilePath = entry.FilePath,
            DirectoryPath = directory
        };
        foreach (var svc in entry.Services)
        {
            compose.Services.Add(new DockerService
            {
                Name = svc.Name,
                Image = svc.Image,
                ContainerName = svc.ContainerName,
                Ports = svc.Ports,
                ComposeFilePath = entry.FilePath,
                WorkingDirectory = directory
            });
        }
        return compose;
    }

    public void Store(string directory, List<CachedFileStamp> stamps, ComposeFile composeFile)
    {
        _entries[directory] = new CachedDirectoryDto(
            stamps,
            composeFile.FileName,
            composeFile.FilePath,
            composeFile.Services
                .Select(s => new CachedServiceDto(s.Name, s.Image, s.ContainerName, s.Ports))
                .ToList());
    }

    public async Task LoadAsync()
    {
        if (_loaded) return;
        await _loadLock.WaitAsync();
        try
        {
            if (_loaded) return;
            _loaded = true;

            if (!File.Exists(_cachePath)) return;
            var json = await File.ReadAllTextAsync(_cachePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, CachedDirectoryDto>>(json);
            if (data == null) return;
            foreach (var (key, value) in data)
                _entries[key] = value;
        }
        catch
        {
            // 快取損毀時直接忽略，掃描會重建
            _entries.Clear();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task SaveAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var json = JsonSerializer.Serialize(
                _entries.ToDictionary(kv => kv.Key, kv => kv.Value));
            await File.WriteAllTextAsync(_cachePath, json);
        }
        catch
        {
            // 寫入失敗只損失快取效益，不影響功能
        }
        finally
        {
            _loadLock.Release();
        }
    }
}
