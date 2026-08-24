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
    public int BuildKitParallelism { get; set; } = 0;

    private string ComposeCommand => UseComposeV2 ? "docker" : "docker-compose";
    private string[] ComposeArgs => UseComposeV2 ? ["compose"] : [];

    private bool IsWsl2 => DockerMode == DockerMode.Wsl2;

    private static readonly Regex WslUncRegex = new(
        @"^[\\/]{2}wsl(\$|\.localhost)[\\/]([^\\/]+)([\\/].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ConvertToWslPath(string windowsPath)
    {
        if (string.IsNullOrEmpty(windowsPath)) return windowsPath;

        var uncMatch = WslUncRegex.Match(windowsPath);
        if (uncMatch.Success)
        {
            var rest = uncMatch.Groups[3].Value.Replace('\\', '/');
            return rest.Length == 0 ? "/" : rest;
        }

        var match = Regex.Match(windowsPath, @"^([A-Za-z]):[\\\/](.*)$");
        if (!match.Success) return windowsPath;

        var drive = match.Groups[1].Value.ToLowerInvariant();
        var rest2 = match.Groups[2].Value.Replace('\\', '/');
        return $"/mnt/{drive}/{rest2}";
    }

    public static bool IsWslUncPath(string path) =>
        !string.IsNullOrEmpty(path) && WslUncRegex.IsMatch(path);

    internal string? NormalizeComposeOverridePath(string? extraOverrideFile) =>
        IsWsl2 && !string.IsNullOrEmpty(extraOverrideFile)
            ? ConvertToWslPath(extraOverrideFile)
            : extraOverrideFile;

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
            var (exitCode, _) = await RunCommandAsync("docker", ["version", "--format", "json"], null, ct, TimeSpan.FromSeconds(10));
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<ContainerInfo>?> GetRunningContainersAsync(CancellationToken ct = default)
    {
        var containers = new List<ContainerInfo>();
        try
        {
            var psi = CreatePsi("docker", ["ps", "-a", "--format", "{{json .}}"], null);

            using var process = ProcessLauncher.Start(psi);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            string stdout;
            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                stdout = await stdoutTask;
                await stderrTask;
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            if (process.ExitCode != 0)
            {
                Debug.WriteLine($"[DockerCli] docker ps exit code: {process.ExitCode}");
                return null;
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
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DockerCli] GetRunningContainersAsync error: {ex.Message}");
            return null;
        }

        return containers;
    }

    public ProcessStream StartLogStream(string containerNameOrId)
    {
        var psi = CreatePsi("docker", ["logs", "-f", "--tail", "200", containerNameOrId], null);

        var process = ProcessLauncher.Start(psi);
        return new ProcessStream(process);
    }

    public ProcessStream StartComposeLogStream(string workingDirectory, string serviceName)
    {
        var composeArgs = BuildComposeArgs(workingDirectory, ["logs", "-f", "--tail", "200", serviceName]);
        var psi = CreatePsi(ComposeCommand, composeArgs, workingDirectory);

        var process = ProcessLauncher.Start(psi);
        return new ProcessStream(process);
    }

    public ProcessStream StartComposeWatch(string workingDirectory, IEnumerable<string> serviceNames)
    {
        var args = BuildComposeArgs(workingDirectory, ["watch", "--no-up"]);
        args.AddRange(serviceNames);

        // watch 子程序內會觸發 rebuild，須同樣注入 build env
        var buildEnv = GetBuildEnv(BuildKitParallelism);
        var psi = CreatePsi(ComposeCommand, args, workingDirectory, IsWsl2 ? buildEnv : null);
        if (!IsWsl2)
            foreach (var kv in buildEnv)
                psi.Environment[kv.Key] = kv.Value;

        var process = ProcessLauncher.Start(psi);
        return new ProcessStream(process);
    }

    private List<string> BuildComposeArgs(
        string workingDirectory, IEnumerable<string> commandArgs, string? extraOverrideFile = null)
    {
        var args = new List<string>(ComposeArgs);
        args.AddRange(ComposeFileHelper.GetComposeFileArgs(workingDirectory));
        if (!string.IsNullOrEmpty(extraOverrideFile))
        {
            args.Add("-f");
            args.Add(extraOverrideFile);
        }
        args.AddRange(commandArgs);
        return args;
    }

    public async Task<(int ExitCode, string Output)> ComposeUpFastWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default)
    {
        var args = BuildComposeArgs(workingDirectory, ["up", "-d", "--remove-orphans"]);
        if (!string.IsNullOrEmpty(serviceName))
            args.Add(serviceName);

        return await RunCommandWithLogAsync(ComposeCommand, args, workingDirectory, onOutput, ct);
    }

    public async Task<(int ExitCode, string Output)> ComposeUpAllAsync(
        string workingDirectory, Action<string> onOutput,
        CancellationToken ct, string? extraOverrideFile = null)
    {
        // 整包 up --build：--build 才會建自訂 nginx（否則吃到本機公開 nginx image，路由設定沒進去 → 404）
        // .NET 服務的 fastdev override 為 build.target: base，--build 只建 runtime 階段，仍很快
        var args = BuildComposeArgs(
            workingDirectory, ["up", "-d", "--build"], NormalizeComposeOverridePath(extraOverrideFile));
        return await RunCommandWithLogAsync(ComposeCommand, args, workingDirectory, onOutput, ct);
    }

    public Task<(int ExitCode, string Output)> RestartContainerAsync(
        string containerNameOrId, Action<string> onOutput, CancellationToken ct) =>
        RunCommandWithLogAsync("docker", ["restart", containerNameOrId], null, onOutput, ct);

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

    public async Task<(int ExitCode, string Output)> ComposeStopWithLogAsync(
        string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default)
    {
        var args = BuildComposeArgs(workingDirectory, ["stop"]);
        if (!string.IsNullOrEmpty(serviceName))
            args.Add(serviceName);

        return await RunCommandWithLogAsync(ComposeCommand, args, workingDirectory, onOutput, ct);
    }

    public async Task<string> GetContainerLogsAsync(string containerNameOrId, int tail = 30, CancellationToken ct = default)
    {
        var (exitCode, output) = await RunCommandAsync("docker", ["logs", "--tail", tail.ToString(), containerNameOrId], null, ct);
        return exitCode == 0 ? output : string.Empty;
    }

    public ProcessStream StartDockerEvents()
    {
        var psi = CreatePsi("docker", ["events", "--format", "{{json .}}", "--filter", "type=container"], null);

        var process = ProcessLauncher.Start(psi);
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

    public Task<(int ExitCode, string Output)> DockerSystemPruneAsync(
        bool all, bool includeVolumes, Action<string> onOutput, CancellationToken ct = default)
    {
        var argList = new List<string> { "system", "prune", "-f" };
        if (all) argList.Add("-a");
        if (includeVolumes) argList.Add("--volumes");
        return RunCommandWithLogAsync("docker", argList, null, onOutput, ct);
    }

    private async Task<(int ExitCode, string Output)> RunCommandAsync(
        string command, IEnumerable<string> args, string? workingDirectory, CancellationToken ct, TimeSpan? timeout = null)
    {
        var psi = CreatePsi(command, args, workingDirectory);

        using var process = ProcessLauncher.Start(psi);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromMinutes(3));

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

    public static Dictionary<string, string> GetBuildEnv(int buildKitParallelism)
    {
        var env = new Dictionary<string, string> { ["COMPOSE_BAKE"] = "true" };
        if (buildKitParallelism > 0)
            env["BUILDKIT_MAX_PARALLELISM"] = buildKitParallelism.ToString();
        return env;
    }

    // withBuildEnv=true 時設定 COMPOSE_BAKE 與 BUILDKIT_MAX_PARALLELISM
    private async Task<(int ExitCode, string Output)> RunCommandWithLogAsync(
        string command, IEnumerable<string> args, string? workingDirectory,
        Action<string> onOutput, CancellationToken ct, bool withBuildEnv = false)
    {
        IReadOnlyDictionary<string, string>? wslEnvOverrides = null;
        if (withBuildEnv && IsWsl2)
            wslEnvOverrides = GetBuildEnv(BuildKitParallelism);

        var psi = CreatePsi(command, args, workingDirectory, wslEnvOverrides);
        psi.Environment["DOCKER_BUILDKIT"] = "1";
        psi.Environment["COMPOSE_DOCKER_CLI_BUILD"] = "1";
        if (withBuildEnv && !IsWsl2)
            foreach (var kv in GetBuildEnv(BuildKitParallelism))
                psi.Environment[kv.Key] = kv.Value;

        using var process = ProcessLauncher.Start(psi);
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
