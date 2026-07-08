using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DockerDashboard.Services;

public class ComposeWatchService : IDisposable
{
    private readonly IDockerCliService _dockerCli;
    private readonly Dictionary<string, WatchEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    public Action<string>? OnOutput { get; set; }
    public Action<string, int>? OnProcessExited { get; set; }

    public ComposeWatchService(IDockerCliService dockerCli) => _dockerCli = dockerCli;

    private sealed class WatchEntry
    {
        public required ProcessStream Stream { get; init; }
        public required HashSet<string> Services { get; init; }
        // 主動停止時設 true，PumpAsync 據此區分異常退出
        public bool StoppedByUs;
    }

    public void SetWatchedServices(string workingDirectory, IReadOnlyCollection<string> serviceNames)
    {
        lock (_lock)
        {
            if (_disposed) return;

            if (_entries.TryGetValue(workingDirectory, out var existing))
            {
                if (existing.Services.SetEquals(serviceNames)) return;
                existing.StoppedByUs = true;
                existing.Stream.Kill();
                _entries.Remove(workingDirectory);
            }

            if (serviceNames.Count == 0) return;

            var entry = new WatchEntry
            {
                Stream = _dockerCli.StartComposeWatch(workingDirectory, serviceNames),
                Services = new HashSet<string>(serviceNames, StringComparer.OrdinalIgnoreCase)
            };
            _entries[workingDirectory] = entry;
            _ = PumpAsync(workingDirectory, entry);
        }
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            foreach (var entry in _entries.Values)
            {
                entry.StoppedByUs = true;
                entry.Stream.Kill();
            }
            _entries.Clear();
        }
    }

    private async Task PumpAsync(string workingDirectory, WatchEntry entry)
    {
        var name = Path.GetFileName(workingDirectory.TrimEnd('\\', '/'));
        try
        {
            var stdoutTask = PumpReaderAsync(entry.Stream.StandardOutput, name);
            var stderrTask = PumpReaderAsync(entry.Stream.StandardError, name);
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch
        {
            // 程序被 Kill 時 reader 可能拋出，屬預期
        }

        bool wasStopped;
        lock (_lock)
        {
            wasStopped = entry.StoppedByUs || _disposed;
            if (_entries.TryGetValue(workingDirectory, out var current) && ReferenceEquals(current, entry))
                _entries.Remove(workingDirectory);
        }

        int exitCode = -1;
        try { exitCode = entry.Stream.ExitCode; } catch { }
        entry.Stream.Dispose();

        if (!wasStopped)
            OnProcessExited?.Invoke(workingDirectory, exitCode);
    }

    private async Task PumpReaderAsync(StreamReader reader, string name)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length > 0)
                OnOutput?.Invoke($"[watch:{name}] {line}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearAll();
        GC.SuppressFinalize(this);
    }
}
