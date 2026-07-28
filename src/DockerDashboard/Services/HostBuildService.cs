using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        string? solutionPath, IReadOnlyList<string> serviceProjectFullPaths,
        Action<string> onOutput, CancellationToken ct)
    {
        // 有 solution + 服務專案清單 → 產暫時 .slnf 只 build 這些服務（相依如 SharedLibrary 自動含入），跳過 Tests、單次呼叫
        if (!string.IsNullOrEmpty(solutionPath) && serviceProjectFullPaths.Count > 0)
        {
            string? slnf = null;
            try
            {
                slnf = WriteSolutionFilter(solutionPath, serviceProjectFullPaths);
                return await RunDotnetBuildAsync(slnf, onOutput, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                onOutput($"[.slnf 產生失敗，改 build 整包 solution] {ex.Message}");
                return await RunDotnetBuildAsync(solutionPath, onOutput, ct);
            }
            finally
            {
                if (slnf != null) try { File.Delete(slnf); } catch { }
            }
        }

        if (!string.IsNullOrEmpty(solutionPath))
            return await RunDotnetBuildAsync(solutionPath, onOutput, ct);

        var aggregate = new StringBuilder();
        foreach (var project in serviceProjectFullPaths)
        {
            var (code, output) = await RunDotnetBuildAsync(project, onOutput, ct);
            aggregate.AppendLine(output);
            if (code != 0) return (code, aggregate.ToString());
        }
        return (0, aggregate.ToString());
    }

    // .slnf 只列服務專案（projects 相對 solution 目錄）；檔案放暫存不污染 repo
    private static string WriteSolutionFilter(string solutionPath, IReadOnlyList<string> serviceProjectFullPaths)
    {
        var slnFull = Path.GetFullPath(solutionPath);
        var slnDir = Path.GetDirectoryName(slnFull)!;
        var projects = serviceProjectFullPaths
            .Select(p => Path.GetRelativePath(slnDir, Path.GetFullPath(p)).Replace('/', '\\'))
            .Select(r => "\"" + r.Replace("\\", "\\\\") + "\"");
        var json = "{\"solution\":{\"path\":\"" + slnFull.Replace("\\", "\\\\") + "\",\"projects\":["
            + string.Join(",", projects) + "]}}";
        var path = Path.Combine(Path.GetTempPath(), "fastdev-" + Guid.NewGuid().ToString("N") + ".slnf");
        File.WriteAllText(path, json);
        return path;
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
        // stdout/stderr 事件在不同執行緒觸發，StringBuilder 非執行緒安全
        var outputLock = new object();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (outputLock) output.AppendLine(e.Data);
                onOutput(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                lock (outputLock) output.AppendLine(e.Data);
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
            lock (outputLock) return (-1, output.ToString() + "\n[build 逾時，已終止]");
        }
        lock (outputLock) return (process.ExitCode, output.ToString());
    }
}
