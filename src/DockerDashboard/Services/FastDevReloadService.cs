using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DockerDashboard.Services;

public class FastDevReloadService : IDisposable
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _debounceCts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    public Func<string, Task>? OnSolutionChanged { get; set; }
    public TimeSpan DebounceDelay { get; set; } = TimeSpan.FromSeconds(2);

    public void Watch(string solutionDir)
    {
        lock (_lock)
        {
            if (_disposed || _watchers.ContainsKey(solutionDir) || !Directory.Exists(solutionDir)) return;

            var watcher = new FileSystemWatcher(solutionDir)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };
            watcher.Changed += (_, e) => OnEvent(solutionDir, e.FullPath);
            watcher.Created += (_, e) => OnEvent(solutionDir, e.FullPath);
            watcher.Renamed += (_, e) => OnEvent(solutionDir, e.FullPath);
            _watchers[solutionDir] = watcher;
        }
    }

    public void Unwatch(string solutionDir)
    {
        lock (_lock)
        {
            if (_watchers.Remove(solutionDir, out var watcher))
                watcher.Dispose();
            if (_debounceCts.Remove(solutionDir, out var cts))
                cts.Cancel();
        }
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            foreach (var watcher in _watchers.Values)
                watcher.Dispose();
            _watchers.Clear();

            foreach (var cts in _debounceCts.Values)
                cts.Cancel();
            _debounceCts.Clear();
        }
    }

    private void OnEvent(string solutionDir, string changedPath)
    {
        var relative = Path.GetRelativePath(solutionDir, changedPath);
        if (ShouldSkip(relative)) return;

        // 只關心 C# 原始碼；bin/obj 內的產物變動不重觸發
        if (!changedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;

        TriggerDebounce(solutionDir);
    }

    private static bool ShouldSkip(string relativePath)
    {
        var parts = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("obj", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void TriggerDebounce(string solutionDir)
    {
        CancellationTokenSource newCts;
        lock (_lock)
        {
            if (_debounceCts.TryGetValue(solutionDir, out var existing))
                existing.Cancel();

            newCts = new CancellationTokenSource();
            _debounceCts[solutionDir] = newCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelay, newCts.Token);
                if (OnSolutionChanged != null)
                    await OnSolutionChanged(solutionDir);
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (_lock)
                {
                    if (_debounceCts.TryGetValue(solutionDir, out var current) &&
                        ReferenceEquals(current, newCts))
                        _debounceCts.Remove(solutionDir);
                }
                newCts.Dispose();
            }
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearAll();
        GC.SuppressFinalize(this);
    }
}
