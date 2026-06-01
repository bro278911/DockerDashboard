using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DockerDashboard.Models;

namespace DockerDashboard.Services;

public class ContainerMonitorService : IDisposable
{
    private readonly IDockerCliService _dockerCli;
    private readonly object _stateLock = new();
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private Task? _eventsTask;
    private ProcessStream? _eventsProcess;
    private Dictionary<string, ContainerStatus> _previousStates = new();

    public event Action<List<ContainerInfo>>? ContainersUpdated;
    public event Action<string, ContainerStatus>? ContainerCrashed;

    public ContainerMonitorService(IDockerCliService dockerCli)
    {
        _dockerCli = dockerCli;
    }

    public void Start(TimeSpan interval)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(interval);
        _monitorTask = MonitorLoop(_cts.Token);
        _eventsTask = EventsLoop(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _eventsProcess?.Dispose();
        _eventsProcess = null;
        try { _monitorTask?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        try { _eventsTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _monitorTask = null;
        _eventsTask = null;
        _timer?.Dispose();
        _timer = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task MonitorLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var containers = await _dockerCli.GetRunningContainersAsync(ct);
                DetectCrashes(containers);
                ContainersUpdated?.Invoke(containers);

                if (_timer != null)
                    await _timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ContainerMonitor] MonitorLoop error: {ex.Message}");
            }
        }
    }

    private async Task EventsLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ProcessStream? stream = null;
            try
            {
                stream = _dockerCli.StartDockerEvents();
                if (ct.IsCancellationRequested)
                    break;

                _eventsProcess = stream;
                var reader = stream.StandardOutput;

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;

                    if (IsContainerStateEvent(line))
                    {
                        await Task.Delay(500, ct);
                        await ForceRefreshAsync();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ContainerMonitor] EventsLoop error: {ex.Message}");
            }
            finally
            {
                stream?.Dispose();
                if (ReferenceEquals(_eventsProcess, stream))
                    _eventsProcess = null;
            }

            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(3000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static bool IsContainerStateEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Action", out var action))
            {
                var act = action.GetString() ?? "";
                return act is "start" or "stop" or "die" or "kill" or "pause" or "unpause" or "restart";
            }
        }
        catch { }
        return false;
    }

    private void DetectCrashes(List<ContainerInfo> containers)
    {
        var crashes = new List<(string Name, ContainerStatus Status)>();

        lock (_stateLock)
        {
            var newStates = new Dictionary<string, ContainerStatus>();

            foreach (var c in containers)
            {
                var key = c.Names.TrimStart('/');
                var status = ParseStatus(c.State);
                newStates[key] = status;

                if (_previousStates.TryGetValue(key, out var prev) &&
                    prev == ContainerStatus.Running &&
                    (status == ContainerStatus.Exited || status == ContainerStatus.Dead))
                {
                    crashes.Add((key, status));
                }
            }

            _previousStates = newStates;
        }

        foreach (var (name, status) in crashes)
            ContainerCrashed?.Invoke(name, status);
    }

    public async Task ForceRefreshAsync()
    {
        try
        {
            var containers = await _dockerCli.GetRunningContainersAsync();
            DetectCrashes(containers);
            ContainersUpdated?.Invoke(containers);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ContainerMonitor] ForceRefreshAsync error: {ex.Message}");
        }
    }

    public static ContainerStatus ParseStatus(string state)
    {
        return state.ToLowerInvariant() switch
        {
            "running" => ContainerStatus.Running,
            "exited" => ContainerStatus.Exited,
            "dead" => ContainerStatus.Dead,
            "restarting" => ContainerStatus.Restarting,
            "paused" => ContainerStatus.Paused,
            _ => ContainerStatus.Unknown
        };
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
