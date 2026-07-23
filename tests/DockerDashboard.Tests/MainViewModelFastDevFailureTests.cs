using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using DockerDashboard.Models;
using DockerDashboard.Services;
using DockerDashboard.ViewModels;

namespace DockerDashboard.Tests;

public class MainViewModelFastDevFailureTests
{
    [Fact]
    public async Task ToggleFastDev_停用Compose失敗時保留FastDev狀態與設定()
    {
        var tempDir = CreateTempDirectory();
        var service = CreateService(tempDir, isFastDev: true);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var settingsService = new SettingsService(settingsPath);
        var settings = CreateFastDevSettings(service, watchEnabled: false);
        await settingsService.SaveAsync(settings);
        FastDevOverrideStore.Write(service.WatchKey, "services: {}");
        var cli = new FakeDockerCliService { ComposeUpNoDepsExitCode = 1 };

        try
        {
            using var viewModel = CreateViewModel(cli, settingsService, tempDir, out _, out _);

            await viewModel.ToggleFastDevServiceCommand.ExecuteAsync(service);

            var persisted = await settingsService.LoadAsync();
            Assert.True(service.IsFastDev);
            Assert.Contains(service.WatchKey, persisted.FastDevEnabledServiceKeys);
            Assert.Contains(persisted.FastDevConfigs, c => c.ServiceKey == service.WatchKey);
            Assert.True(File.Exists(FastDevOverrideStore.PathFor(service.WatchKey)));
            Assert.Equal($"⚠ {service.Name} 還原可能失敗", viewModel.StatusMessage);
        }
        finally
        {
            FastDevOverrideStore.Delete(service.WatchKey);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ToggleFastDev_啟用Compose失敗時恢復AutoWatch工作階段()
    {
        var tempDir = CreateTempDirectory();
        var service = CreateService(tempDir, isFastDev: false);
        service.IsWatching = true;
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var settingsService = new SettingsService(settingsPath);
        var settings = CreateFastDevSettings(service, watchEnabled: true);
        settings.FastDevEnabledServiceKeys.Clear();
        await settingsService.SaveAsync(settings);
        var cli = new FakeDockerCliService { ComposeUpNoDepsExitCode = 1 };

        try
        {
            using var viewModel = CreateViewModel(cli, settingsService, tempDir, out var watchService, out _);
            watchService.IsEnabled = true;
            watchService.DebounceDelay = TimeSpan.FromMilliseconds(30);
            watchService.AddWatch(service.WorkingDirectory, service.Name);

            await viewModel.ToggleFastDevServiceCommand.ExecuteAsync(service);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Program.cs"), "public class Program {}");
            await cli.RebuildTriggered.Task.WaitAsync(TimeSpan.FromSeconds(3));

            var persisted = await settingsService.LoadAsync();
            Assert.False(service.IsFastDev);
            Assert.True(service.IsWatching);
            Assert.Contains(service.WatchKey, persisted.WatchEnabledServiceKeys);
            Assert.DoesNotContain(service.WatchKey, persisted.FastDevEnabledServiceKeys);
            Assert.False(File.Exists(FastDevOverrideStore.PathFor(service.WatchKey)));
            Assert.Equal($"⚠ {service.Name} Fast Dev 啟用失敗", viewModel.StatusMessage);
        }
        finally
        {
            FastDevOverrideStore.Delete(service.WatchKey);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ToggleFastDev_Wsl啟用失敗時停止並恢復ComposeWatch()
    {
        var tempDir = CreateTempDirectory();
        var service = CreateService(tempDir, isFastDev: false);
        service.WorkingDirectory = $@"\\wsl.localhost\Ubuntu\home\user\api-{Guid.NewGuid():N}";
        service.ComposeFilePath = $@"{service.WorkingDirectory}\compose.yml";
        service.IsWatching = true;
        var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
        var settings = CreateFastDevSettings(service, watchEnabled: true);
        settings.AutoWatchEnabled = true;
        settings.FastDevEnabledServiceKeys.Clear();
        await settingsService.SaveAsync(settings);
        var cli = new FakeDockerCliService { ComposeUpNoDepsExitCode = 1 };

        try
        {
            using var viewModel = CreateViewModel(
                cli, settingsService, tempDir, out _, out var composeWatchService);
            viewModel.ApplyWatchSettings(settings);
            var project = new DockerProject { Name = "WSL API", FolderPath = service.WorkingDirectory };
            var composeFile = new ComposeFile
            {
                FileName = "compose.yml",
                FilePath = service.ComposeFilePath,
                DirectoryPath = service.WorkingDirectory,
            };
            composeFile.Services.Add(service);
            project.ComposeFiles.Add(composeFile);
            viewModel.Projects.Add(project);
            composeWatchService.SetWatchedServices(service.WorkingDirectory, [service.Name]);

            await viewModel.ToggleFastDevServiceCommand.ExecuteAsync(service);

            Assert.True(service.IsWatching);
            Assert.Equal(2, cli.ComposeWatchStarts.Count);
            Assert.All(cli.ComposeWatchStarts, start =>
            {
                Assert.Equal(service.WorkingDirectory, start.WorkingDirectory);
                Assert.Equal([service.Name], start.ServiceNames);
            });
            await cli.ComposeWatchExits[0].Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(cli.ComposeWatchExits[1].Task.IsCompleted);
        }
        finally
        {
            FastDevOverrideStore.Delete(service.WatchKey);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static MainViewModel CreateViewModel(
        FakeDockerCliService cli,
        SettingsService settingsService,
        string tempDir,
        out WatchRebuildService watchService,
        out ComposeWatchService composeWatchService)
    {
        watchService = new WatchRebuildService();
        composeWatchService = new ComposeWatchService(cli);
        return new MainViewModel(
            cli,
            new FakeGitService(),
            new ComposeFileScanner(new ScanCacheService(Path.Combine(tempDir, "scan-cache.json"))),
            settingsService,
            new ContainerMonitorService(cli),
            watchService,
            composeWatchService,
            (UpdateService)RuntimeHelpers.GetUninitializedObject(typeof(UpdateService)));
    }

    private static AppSettings CreateFastDevSettings(DockerService service, bool watchEnabled)
    {
        var settings = new AppSettings
        {
            FastDevEnabledServiceKeys = [service.WatchKey],
            FastDevConfigs =
            [
                new FastDevConfig
                {
                    ServiceKey = service.WatchKey,
                    CsprojRelativePath = "Api.csproj",
                    RuntimeImage = "mcr.microsoft.com/dotnet/sdk:10.0",
                    SrcRoot = service.WorkingDirectory,
                },
            ],
        };
        if (watchEnabled)
            settings.WatchEnabledServiceKeys.Add(service.WatchKey);
        return settings;
    }

    private static DockerService CreateService(string tempDir, bool isFastDev) => new()
    {
        Name = $"api-{Guid.NewGuid():N}",
        ComposeFilePath = Path.Combine(tempDir, "compose.yml"),
        WorkingDirectory = tempDir,
        IsFastDev = isFastDev,
    };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"DockerDashboard.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeGitService : IGitService
    {
        public bool IsGitRepository(string folderPath) => false;
        public Task<string> GetCurrentBranchAsync(string folderPath) => Task.FromResult(string.Empty);
        public Task<List<string>> GetLocalBranchesAsync(string folderPath) => Task.FromResult<List<string>>([]);
        public Task<List<string>> GetRemoteBranchesAsync(string folderPath) => Task.FromResult<List<string>>([]);
        public Task<bool> IsDirtyAsync(string folderPath) => Task.FromResult(false);
        public Task<(bool Success, string Output)> CheckoutAsync(string folderPath, string branchName) =>
            Task.FromResult((true, string.Empty));
    }

    private sealed class FakeDockerCliService : IDockerCliService
    {
        public int ComposeUpNoDepsExitCode { get; init; }
        public TaskCompletionSource RebuildTriggered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string WorkingDirectory, string[] ServiceNames)> ComposeWatchStarts { get; } = [];
        public List<TaskCompletionSource> ComposeWatchExits { get; } = [];
        public bool UseComposeV2 { get; set; } = true;
        public DockerMode DockerMode { get; set; }
        public string WslDistroName { get; set; } = string.Empty;
        public int BuildKitParallelism { get; set; }

        public Task<bool> IsDockerAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<List<ContainerInfo>?> GetRunningContainersAsync(CancellationToken ct = default) =>
            Task.FromResult<List<ContainerInfo>?>([]);
        public Task<(int ExitCode, string Output)> ComposeUpNoDepsAsync(
            string workingDirectory,
            string serviceName,
            Action<string> onOutput,
            CancellationToken ct,
            string? extraOverrideFile = null) =>
            Task.FromResult((ComposeUpNoDepsExitCode, string.Empty));
        public Task<(int ExitCode, string Output)> ComposeRebuildRestartWithLogAsync(
            string workingDirectory,
            Action<string> onOutput,
            string? serviceName = null,
            CancellationToken ct = default)
        {
            RebuildTriggered.TrySetResult();
            return Task.FromResult((0, string.Empty));
        }

        public ProcessStream StartLogStream(string containerNameOrId) => throw new NotSupportedException();
        public ProcessStream StartComposeLogStream(string workingDirectory, string serviceName) => throw new NotSupportedException();
        public ProcessStream StartComposeWatch(string workingDirectory, IEnumerable<string> serviceNames)
        {
            ComposeWatchStarts.Add((workingDirectory, serviceNames.ToArray()));
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ComposeWatchExits.Add(exited);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 30 > nul")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
                EnableRaisingEvents = true,
            };
            process.Exited += (_, _) => exited.TrySetResult();
            process.Start();
            return new ProcessStream(process);
        }
        public Task<(int ExitCode, string Output)> ComposeUpFastWithLogAsync(string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> ComposeDownWithLogAsync(string workingDirectory, Action<string> onOutput, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> ComposeRestartWithLogAsync(string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> ComposeForceRebuildWithLogAsync(string workingDirectory, Action<string> onOutput, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> ComposeStopWithLogAsync(string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> ComposePullWithLogAsync(string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetContainerLogsAsync(string containerNameOrId, int tail = 30, CancellationToken ct = default) => throw new NotSupportedException();
        public ProcessStream StartDockerEvents() => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> DockerImagePruneAsync(bool all, Action<string> onOutput, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> DockerVolumePruneAsync(Action<string> onOutput, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> DockerSystemPruneAsync(bool all, bool includeVolumes, Action<string> onOutput, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
