using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;

namespace DockerDashboard.Services;

internal static class ComposeFileHelper
{
    private static readonly string[] MainFiles =
        ["docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml"];

    private static readonly string[] OverrideFiles =
        ["docker-compose.override.yml", "docker-compose.override.yaml"];

    private static readonly string[] BuildFiles =
        ["docker-compose.build.yml", "docker-compose.build.yaml"];

    private static readonly ConcurrentDictionary<string, CacheEntry> ComposeFileArgsCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static List<string> GetComposeFileArgs(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return [];

        var normalized = Path.GetFullPath(directory);
        var stampUtcTicks = Directory.GetLastWriteTimeUtc(normalized).Ticks;
        if (ComposeFileArgsCache.TryGetValue(normalized, out var existing) &&
            existing.DirectoryStampUtcTicks == stampUtcTicks)
        {
            return [.. existing.Args];
        }

        var cachedArgs = BuildArgs(normalized);
        ComposeFileArgsCache[normalized] = new CacheEntry(stampUtcTicks, cachedArgs);
        return [.. cachedArgs];
    }

    private static string[] BuildArgs(string directory)
    {
        var args = new List<string>();
        var mainFile = System.Array.Find(MainFiles, f => File.Exists(Path.Combine(directory, f)));
        if (mainFile == null) return [];

        args.Add("-f");
        args.Add(mainFile);

        var overrideFile = System.Array.Find(OverrideFiles, f => File.Exists(Path.Combine(directory, f)));
        if (overrideFile != null)
        {
            args.Add("-f");
            args.Add(overrideFile);
        }

        var buildFile = System.Array.Find(BuildFiles, f => File.Exists(Path.Combine(directory, f)));
        if (buildFile != null)
        {
            args.Add("-f");
            args.Add(buildFile);
        }

        return [.. args];
    }

    public static void InvalidateCache(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        ComposeFileArgsCache.TryRemove(Path.GetFullPath(directory), out _);
    }

    public static void ClearCache()
    {
        ComposeFileArgsCache.Clear();
    }

    private readonly record struct CacheEntry(long DirectoryStampUtcTicks, string[] Args);
}
