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

    Task<bool> IsDockerAvailableAsync(CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeUpAsync(
        string workingDirectory, string? serviceName = null, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeDownAsync(
        string workingDirectory, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeRestartAsync(
        string workingDirectory, string? serviceName = null, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeStopAsync(
        string workingDirectory, string? serviceName = null, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeStartAsync(
        string workingDirectory, string? serviceName = null, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposePullAsync(
        string workingDirectory, string? serviceName = null, CancellationToken ct = default);

    Task<List<ContainerInfo>> GetRunningContainersAsync(CancellationToken ct = default);

    ProcessStream StartLogStream(string containerNameOrId);

    ProcessStream StartComposeLogStream(string workingDirectory, string serviceName);

    Task<(int ExitCode, string Output)> ComposeUpWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeUpFastWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeDownWithLogAsync(
        string workingDirectory, Action<string> onOutput, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeRestartWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeRebuildRestartWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeForceRebuildWithLogAsync(
        string workingDirectory, Action<string> onOutput, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeStartWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposeStopWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default);

    Task<(int ExitCode, string Output)> ComposePullWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default);

    ProcessStream StartDockerEvents();
}
