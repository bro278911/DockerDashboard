using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DockerDashboard.Models;


namespace DockerDashboard.Services;

public class DockerCliService : IDockerCliService
{
    public bool UseComposeV2 { get; set; } = true;
    public DockerMode DockerMode { get; set; } = DockerMode.DockerDesktop;
    public string WslDistroName { get; set; } = "Ubuntu";

    private string ComposeCommand => UseComposeV2 ? "docker" : "docker-compose";
    private string[] ComposeArgs => UseComposeV2 ? ["compose"] : [];

    private bool IsWsl2 => DockerMode == DockerMode.Wsl2;

    public static string ConvertToWslPath(string windowsPath)
    {
        if (string.IsNullOrEmpty(windowsPath)) return windowsPath;

        var match = Regex.Match(windowsPath, @"^([A-Za-z]):[\\\/](.*)$");
        if (!match.Success) return windowsPath;

        var drive = match.Groups[1].Value.ToLowerInvariant();
        var rest = match.Groups[2].Value.Replace('\\', '/');
        return $"/mnt/{drive}/{rest}";
    }

    private ProcessStartInfo CreatePsi(
        string command,
        IEnumerable<string> args,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? wslEnvOverrides = null)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (IsWsl2)
        {
            psi.FileName = "wsl";
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(WslDistroName);

            if (!string.IsNullOrEmpty(workingDirectory))
            {
                psi.ArgumentList.Add("--cd");
                psi.ArgumentList.Add(ConvertToWslPath(workingDirectory));
            }

            psi.ArgumentList.Add("--");

            if (wslEnvOverrides?.Count > 0)
            {
                psi.ArgumentList.Add("env");
                foreach (var kv in wslEnvOverrides)
                    psi.ArgumentList.Add($"{kv.Key}={kv.Value}");
            }

            psi.ArgumentList.Add(command);
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
        }
        else
        {
            psi.FileName = command;
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            if (!string.IsNullOrEmpty(workingDirectory))
                psi.WorkingDirectory = workingDirectory;
        }

        return psi;
    }

    public async Task<bool> IsDockerAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var (exitCode, _) = await RunCommandAsync("docker", ["version", "--format", "json"], null, ct);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(int ExitCode, string Output)> ComposeUpAsync(
        string workingDirectory, string? serviceName = null, CancellationToken ct = default)
    {
        var args = BuildComposeArgs(workingDirectory, ["up", "-d", "--build", "--remove-orphans"]);
        if (!string.IsNullOrEmpty(serviceName))
            args.Add(serviceName);

        return await RunCommandAsync(ComposeCommand, args, workingDirectory, ct);
    }

    public async Task<(int ExitCode, string Output)> ComposeDownAsync(
        string workingDirectory, CancellationToken ct = default)
    {
        var args = BuildComposeArgs(workingDirectory, ["down", "--remove-orphans"]);
        return await RunCommandAsync(ComposeCommand, args, workingDirectory, ct);
    }

    public async Task<(int ExitCode, string Output)> ComposeRestartAsync(
        string workingDirectory, string? serviceName = null, CancellationToken ct = default)
    {
        var args = BuildComposeArgs(workingDirectory, ["restart"]);
        if (!string.IsNullOrEmpty(serviceName))
            args.Add(serviceName);

        return await RunCommandAsync(ComposeCommand, args, workingDirectory, ct);
    }

    public async Task<(int ExitCode, string Output)> ComposeStopAsync(
        string workingDirectory, string? serviceName = null, CancellationToken ct = default)
    {
        var args = BuildComposeArgs(workingDirectory, ["stop"]);
        if (!string.IsNullOrEmpty(serviceName))
            args.Add(serviceName);

        return await RunCommandAsync(ComposeCommand, args, workingDirectory, ct);
    }

    public async Task<(int ExitCode, string Output)> ComposeStartAsync(
        string workingDirectory, string? serviceName = null, CancellationToken ct = default)
    {
        var args = BuildComposeArgs(workingDirectory, ["start"]);
        if (!string.IsNullOrEmpty(serviceName))
            args.Add(serviceName);

        return await RunCommandAsync(ComposeCommand, args, workingDirectory, ct);
    }

    public async Task<List<ContainerInfo>> GetRunningContainersAsync(CancellationToken ct = default)
    {
        var containers = new List<ContainerInfo>();
        try
        {
            var psi = CreatePsi("docker", ["ps", "-a", "--format", "{{json .}}"], null);

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            var stdout = await stdoutTask;
            await stderrTask;
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                Debug.WriteLine($"[DockerCli] docker ps exit code: {process.ExitCode}");
                return containers;
            }

            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Debug.WriteLine($"[DockerCli] docker ps returned {lines.Length} lines");

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '{') continue;

                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    var root = doc.RootElement;
                    containers.Add(new ContainerInfo
                    {
                        ID = root.TryGetProperty("ID", out var id) ? id.GetString() ?? "" : "",
                        Names = root.TryGetProperty("Names", out var names) ? names.GetString() ?? "" : "",
                        Image = root.TryGetProperty("Image", out var image) ? image.GetString() ?? "" : "",
                        State = root.TryGetProperty("State", out var state) ? state.GetString() ?? "" : "",
                        Status = root.TryGetProperty("Status", out var status) ? status.GetString() ?? "" : "",
                        Ports = root.TryGetProperty("Ports", out var ports) ? ports.GetString() ?? "" : "",
                        Labels = root.TryGetProperty("Labels", out var labels) ? labels.GetString() ?? "" : ""
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DockerCli] JSON parse error: {ex.Message}");
                }
            }

            Debug.WriteLine($"[DockerCli] Parsed {containers.Count} containers");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DockerCli] GetRunningContainersAsync error: {ex.Message}");
        }

        return containers;
    }

    public ProcessStream StartLogStream(string containerNameOrId)
    {
        var psi = CreatePsi("docker", ["logs", "-f", "--tail", "200", containerNameOrId], null);

        var process = new Process { StartInfo = psi };
        process.Start();
        return new ProcessStream(process);
    }

    public ProcessStream StartComposeLogStream(string workingDirectory, string serviceName)
    {
        var composeArgs = BuildComposeArgs(workingDirectory, ["logs", "-f", "--tail", "200", serviceName]);
        var psi = CreatePsi(ComposeCommand, composeArgs, workingDirectory);

        var process = new Process { StartInfo = psi };
        process.Start();
        return new ProcessStream(process);
    }

    private List<string> BuildComposeArgs(string workingDirectory, IEnumerable<string> commandArgs)
    {
        var args = new List<string>(ComposeArgs);
        args.AddRange(ComposeFileHelper.GetComposeFileArgs(workingDirectory));
        args.AddRange(commandArgs);
        return args;
    }

    public async Task<(int ExitCode, string Output)> ComposeUpWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default)
    {
        var args = BuildComposeArgs(workingDirectory, ["up", "-d", "--build", "--remove-orphans"]);
        if (!string.IsNullOrEmpty(serviceName))
            args.Add(serviceName);

        return await RunCommandWithLogAsync(ComposeCommand, args, workingDirectory, onOutput, ct);
    }

    public async Task<(int ExitCode, string Output)> ComposeUpFastWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default)
    {
        var args = BuildComposeArgs(workingDirectory, ["up", "-d", "--remove-orphans"]);
        if (!string.IsNullOrEmpty(serviceName))
            args.Add(serviceName);

        return await RunCommandWithLogAsync(ComposeCommand, args, workingDirectory, onOutput, ct);
    }

    public async Task<(int ExitCode, string Output)> ComposeDownWithLogAsync(
        string workingDirectory, Action<string> onOutput, CancellationToken ct = default)
    {
        var args = BuildComposeArgs(workingDirectory, ["down", "--remove-orphans"]);
        return await RunCommandWithLogAsync(ComposeCommand, args, workingDirectory, onOutput, ct);
    }

    public async Task<(int ExitCode, string Output)> ComposeRestartWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default)
    {
        var args = BuildComposeArgs(workingDirectory, ["restart"]);
        if (!string.IsNullOrEmpty(serviceName))
            args.Add(serviceName);

        return await RunCommandWithLogAsync(ComposeCommand, args, workingDirectory, onOutput, ct);
    }

    public async Task<(int ExitCode, string Output)> ComposeRebuildRestartWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default)
    {
        // --no-deps: 只重建目標 service，跳過 depends_on 依賴（依賴本來就在跑時省去額外 round-trip）
        var subcommands = serviceName != null
            ? (IEnumerable<string>)["up", "-d", "--build", "--no-deps"]
            : ["up", "-d", "--build"];
        var args = BuildComposeArgs(workingDirectory, subcommands);
        if (!string.IsNullOrEmpty(serviceName))
            args.Add(serviceName);

        return await RunCommandWithLogAsync(ComposeCommand, args, workingDirectory, onOutput, ct, withBuildEnv: true);
    }

    public async Task<(int ExitCode, string Output)> ComposeForceRebuildWithLogAsync(
        string workingDirectory, Action<string> onOutput, CancellationToken ct = default)
    {
        var buildArgs = BuildComposeArgs(workingDirectory, ["build", "--no-cache"]);
        var (buildExit, _) = await RunCommandWithLogAsync(
            ComposeCommand, buildArgs, workingDirectory, onOutput, ct, withBuildEnv: true);
        if (buildExit != 0)
            return (buildExit, string.Empty);

        var upArgs = BuildComposeArgs(workingDirectory, ["up", "-d", "--remove-orphans"]);
        return await RunCommandWithLogAsync(ComposeCommand, upArgs, workingDirectory, onOutput, ct);
    }

    public async Task<(int ExitCode, string Output)> ComposeStartWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default)
    {
        var args = BuildComposeArgs(workingDirectory, ["start"]);
        if (!string.IsNullOrEmpty(serviceName))
            args.Add(serviceName);

        return await RunCommandWithLogAsync(ComposeCommand, args, workingDirectory, onOutput, ct);
    }

    public async Task<(int ExitCode, string Output)> ComposeStopWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default)
    {
        var args = BuildComposeArgs(workingDirectory, ["stop"]);
        if (!string.IsNullOrEmpty(serviceName))
            args.Add(serviceName);

        return await RunCommandWithLogAsync(ComposeCommand, args, workingDirectory, onOutput, ct);
    }

    public async Task<(int ExitCode, string Output)> ComposePullAsync(
        string workingDirectory, string? serviceName = null, CancellationToken ct = default)
    {
        var args = BuildComposeArgs(workingDirectory, ["pull"]);
        if (!string.IsNullOrEmpty(serviceName))
            args.Add(serviceName);

        return await RunCommandAsync(ComposeCommand, args, workingDirectory, ct);
    }

    public async Task<(int ExitCode, string Output)> ComposePullWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default)
    {
        var args = BuildComposeArgs(workingDirectory, ["pull"]);
        if (!string.IsNullOrEmpty(serviceName))
            args.Add(serviceName);

        return await RunCommandWithLogAsync(ComposeCommand, args, workingDirectory, onOutput, ct);
    }

    public ProcessStream StartDockerEvents()
    {
        var psi = CreatePsi("docker", ["events", "--format", "{{json .}}", "--filter", "type=container"], null);

        var process = new Process { StartInfo = psi };
        process.Start();
        return new ProcessStream(process);
    }

    public Task<(int ExitCode, string Output)> DockerImagePruneAsync(
        bool all, Action<string> onOutput, CancellationToken ct = default)
    {
        var args = all
            ? (IEnumerable<string>)["image", "prune", "-af"]
            : ["image", "prune", "-f"];
        return RunCommandWithLogAsync("docker", args, null, onOutput, ct);
    }

    public Task<(int ExitCode, string Output)> DockerVolumePruneAsync(
        Action<string> onOutput, CancellationToken ct = default)
        => RunCommandWithLogAsync("docker", ["volume", "prune", "-f"], null, onOutput, ct);

    public Task<(int ExitCode, string Output)> DockerNetworkPruneAsync(
        Action<string> onOutput, CancellationToken ct = default)
        => RunCommandWithLogAsync("docker", ["network", "prune", "-f"], null, onOutput, ct);

    public Task<(int ExitCode, string Output)> DockerSystemPruneAsync(
        bool all, bool includeVolumes, Action<string> onOutput, CancellationToken ct = default)
    {
        var argList = new List<string> { "system", "prune", "-f" };
        if (all) argList.Add("-a");
        if (includeVolumes) argList.Add("--volumes");
        return RunCommandWithLogAsync("docker", argList, null, onOutput, ct);
    }

    private async Task<(int ExitCode, string Output)> RunCommandAsync(
        string command, IEnumerable<string> args, string? workingDirectory, CancellationToken ct)
    {
        var psi = CreatePsi(command, args, workingDirectory);

        using var process = new Process { StartInfo = psi };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        process.Start();

        string stdout, stderr;
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            stdout = await stdoutTask;
            stderr = await stderrTask;
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, "[操作逾時，已強制終止]");
        }

        var combined = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\n{stderr}";
        return (process.ExitCode, combined);
    }

    // withBuildEnv=true 時設定 BUILDKIT_MAX_PARALLELISM，避免平行 build 資源競爭
    private async Task<(int ExitCode, string Output)> RunCommandWithLogAsync(
        string command, IEnumerable<string> args, string? workingDirectory,
        Action<string> onOutput, CancellationToken ct, bool withBuildEnv = false)
    {
        IReadOnlyDictionary<string, string>? wslEnvOverrides = withBuildEnv && IsWsl2
            ? new Dictionary<string, string> { ["BUILDKIT_MAX_PARALLELISM"] = "1" }
            : null;

        var psi = CreatePsi(command, args, workingDirectory, wslEnvOverrides);
        psi.Environment["DOCKER_BUILDKIT"] = "1";
        psi.Environment["COMPOSE_DOCKER_CLI_BUILD"] = "1";
        if (withBuildEnv && !IsWsl2)
            psi.Environment["BUILDKIT_MAX_PARALLELISM"] = "1";

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                output.AppendLine(e.Data);
                onOutput(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                output.AppendLine(e.Data);
                onOutput(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(30));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, output.ToString() + "\n[操作逾時，已強制終止]");
        }

        return (process.ExitCode, output.ToString());
    }
}
