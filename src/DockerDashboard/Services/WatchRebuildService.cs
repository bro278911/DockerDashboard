using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DockerDashboard.Services;

public class WatchRebuildService : IDisposable
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _watchedServices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _debounceCts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private Func<string, string, Task>? _rebuildCallback;
    private bool _disposed;

    public bool IsEnabled { get; set; }
    public TimeSpan DebounceDelay { get; set; } = TimeSpan.FromSeconds(2);

    public void SetRebuildCallback(Func<string, string, Task> callback) => _rebuildCallback = callback;

    public void AddWatch(string workingDirectory, string serviceName)
    {
        lock (_lock)
        {
            if (!_watchedServices.TryGetValue(workingDirectory, out var services))
            {
                services = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _watchedServices[workingDirectory] = services;
            }
            services.Add(serviceName);

            if (!_watchers.ContainsKey(workingDirectory) && Directory.Exists(workingDirectory))
            {
                var watcher = new FileSystemWatcher(workingDirectory)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };
                watcher.Changed += (_, e) => OnFileEvent(workingDirectory, e.FullPath);
                watcher.Created += (_, e) => OnFileEvent(workingDirectory, e.FullPath);
                watcher.Renamed += (_, e) => OnFileEvent(workingDirectory, e.FullPath);
                _watchers[workingDirectory] = watcher;
            }
        }
    }

    public void RemoveWatch(string workingDirectory, string serviceName)
    {
        lock (_lock)
        {
            if (!_watchedServices.TryGetValue(workingDirectory, out var services)) return;
            services.Remove(serviceName);

            if (services.Count == 0)
            {
                _watchedServices.Remove(workingDirectory);
                if (_watchers.Remove(workingDirectory, out var watcher))
                    watcher.Dispose();
            }
        }
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            foreach (var watcher in _watchers.Values)
                watcher.Dispose();
            _watchers.Clear();
            _watchedServices.Clear();

            foreach (var cts in _debounceCts.Values)
                cts.Cancel();
            _debounceCts.Clear();
        }
    }

    private void OnFileEvent(string workingDirectory, string changedPath)
    {
        if (!IsEnabled) return;

        var relative = Path.GetRelativePath(workingDirectory, changedPath);
        if (ShouldSkip(relative)) return;

        HashSet<string> services;
        lock (_lock)
        {
            if (!_watchedServices.TryGetValue(workingDirectory, out var svcSet)) return;
            services = new HashSet<string>(svcSet, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var serviceName in services)
            TriggerDebounce(workingDirectory, serviceName);
    }

    private static bool ShouldSkip(string relativePath)
    {
        var parts = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (string.Equals(part, ".git", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "node_modules", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void TriggerDebounce(string workingDirectory, string serviceName)
    {
        var key = $"{workingDirectory}::{serviceName}";
        CancellationTokenSource newCts;

        lock (_lock)
        {
            if (_debounceCts.TryGetValue(key, out var existingCts))
                existingCts.Cancel();

            newCts = new CancellationTokenSource();
            _debounceCts[key] = newCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelay, newCts.Token);
                if (_rebuildCallback != null)
                    await _rebuildCallback(workingDirectory, serviceName);
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (_lock)
                {
                    if (_debounceCts.TryGetValue(key, out var current) && ReferenceEquals(current, newCts))
                        _debounceCts.Remove(key);
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
