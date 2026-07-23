using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DockerDashboard.Services;

public class HostBuildService
{
    public static long DllMtimeTicks(string dllPath) =>
        File.Exists(dllPath) ? File.GetLastWriteTimeUtc(dllPath).Ticks : 0;

    public static IReadOnlyList<string> ChangedServiceKeys(
        IReadOnlyDictionary<string, (string DllPath, long BeforeTicks)> before,
        Func<string, long> currentTicksOf)
    {
        var changed = new List<string>();
        foreach (var (serviceKey, info) in before)
            if (currentTicksOf(info.DllPath) > info.BeforeTicks)
                changed.Add(serviceKey);
        return changed;
    }

    public async Task<(int ExitCode, string Output)> BuildAsync(
        string? solutionPath, IReadOnlyList<string> fallbackProjectFullPaths,
        Action<string> onOutput, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(solutionPath))
            return await RunDotnetBuildAsync(solutionPath, onOutput, ct);

        var aggregate = new StringBuilder();
        foreach (var project in fallbackProjectFullPaths)
        {
            var (code, output) = await RunDotnetBuildAsync(project, onOutput, ct);
            aggregate.AppendLine(output);
            if (code != 0) return (code, aggregate.ToString());
        }
        return (0, aggregate.ToString());
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetBuildAsync(
        string target, Action<string> onOutput, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("minimal");

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
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            return (-1, output.ToString() + "\n[build 逾時，已終止]");
        }
        return (process.ExitCode, output.ToString());
    }
}
