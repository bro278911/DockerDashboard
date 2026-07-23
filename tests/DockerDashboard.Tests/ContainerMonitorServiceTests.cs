using DockerDashboard.Models;
using DockerDashboard.Services;

namespace DockerDashboard.Tests;

public class ContainerMonitorServiceTests
{
    [Fact]
    public async Task ForceRefreshAsync_SkipsUpdate_WhenDockerPsFails()
    {
        var cli = new FakeDockerCliService { Containers = null };
        using var monitor = new ContainerMonitorService(cli);
        var updated = false;
        monitor.ContainersUpdated += _ => updated = true;

        await monitor.ForceRefreshAsync();

        Assert.False(updated);
    }

    [Fact]
    public async Task ForceRefreshAsync_RaisesUpdate_WhenDockerPsSucceeds()
    {
        var cli = new FakeDockerCliService { Containers = [] };
        using var monitor = new ContainerMonitorService(cli);
        var updated = false;
        monitor.ContainersUpdated += _ => updated = true;

        await monitor.ForceRefreshAsync();

        Assert.True(updated);
    }

    private sealed class FakeDockerCliService : IDockerCliService
    {
        public List<ContainerInfo>? Containers { get; set; }

        public bool UseComposeV2 { get; set; } = true;
        public DockerMode DockerMode { get; set; }
        public string WslDistroName { get; set; } = "";
        public int BuildKitParallelism { get; set; }

        public Task<bool> IsDockerAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<List<ContainerInfo>?> GetRunningContainersAsync(CancellationToken ct = default) =>
            Task.FromResult(Containers);

        public ProcessStream StartLogStream(string containerNameOrId) => throw new NotSupportedException();
        public ProcessStream StartComposeLogStream(string workingDirectory, string serviceName) => throw new NotSupportedException();
        public ProcessStream StartComposeWatch(string workingDirectory, IEnumerable<string> serviceNames) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> ComposeUpFastWithLogAsync(string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> ComposeUpAllAsync(string workingDirectory, Action<string> onOutput, CancellationToken ct, string? extraOverrideFile = null) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> RestartContainerAsync(string containerNameOrId, Action<string> onOutput, CancellationToken ct) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> ComposeDownWithLogAsync(string workingDirectory, Action<string> onOutput, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> ComposeRestartWithLogAsync(string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> ComposeRebuildRestartWithLogAsync(string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> ComposeStopWithLogAsync(string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetContainerLogsAsync(string containerNameOrId, int tail = 30, CancellationToken ct = default) => throw new NotSupportedException();
        public ProcessStream StartDockerEvents() => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> DockerImagePruneAsync(bool all, Action<string> onOutput, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> DockerVolumePruneAsync(Action<string> onOutput, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> DockerSystemPruneAsync(bool all, bool includeVolumes, Action<string> onOutput, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
