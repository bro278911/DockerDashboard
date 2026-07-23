using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DockerDashboard.Models;

namespace DockerDashboard.Services;

public interface IDockerCliService
{
    bool UseComposeV2 { get; set; }
    DockerMode DockerMode { get; set; }
    string WslDistroName { get; set; }
    int BuildKitParallelism { get; set; }

    Task<bool> IsDockerAvailableAsync(CancellationToken ct = default);

    /// <summary>docker ps 失敗（逾時、非零 exit code、例外）時回傳 null，呼叫端應跳過該輪更新。</summary>
    Task<List<ContainerInfo>?> GetRunningContainersAsync(CancellationToken ct = default);

    ProcessStream StartLogStream(string containerNameOrId);

    ProcessStream StartComposeLogStream(string workingDirectory, string serviceName);

    ProcessStream StartComposeWatch(string workingDirectory, IEnumerable<string> serviceNames);

    Task<(int ExitCode, string Output)> ComposeUpFastWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeUpNoDepsAsync(
        string workingDirectory, string serviceName, Action<string> onOutput,
        CancellationToken ct, string? extraOverrideFile = null);

    Task<(int ExitCode, string Output)> RestartContainerAsync(
        string containerNameOrId, Action<string> onOutput, CancellationToken ct);

    Task<(int ExitCode, string Output)> ComposeDownWithLogAsync(
        string workingDirectory, Action<string> onOutput, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeRestartWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeRebuildRestartWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeStopWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default);

    Task<string> GetContainerLogsAsync(string containerNameOrId, int tail = 30, CancellationToken ct = default);

    ProcessStream StartDockerEvents();

    Task<(int ExitCode, string Output)> DockerImagePruneAsync(
        bool all, Action<string> onOutput, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> DockerVolumePruneAsync(
        Action<string> onOutput, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> DockerSystemPruneAsync(
        bool all, bool includeVolumes, Action<string> onOutput, CancellationToken ct = default);
}
