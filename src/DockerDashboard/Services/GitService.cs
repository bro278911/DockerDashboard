using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DockerDashboard.Services;

public class GitService : IGitService
{
    public bool IsGitRepository(string folderPath)
    {
        return Directory.Exists(Path.Combine(folderPath, ".git"));
    }

    public async Task<string> GetCurrentBranchAsync(string folderPath)
    {
        var (exitCode, output) = await RunGitAsync(folderPath, "rev-parse", "--abbrev-ref", "HEAD");
        return exitCode == 0 ? output.Trim() : string.Empty;
    }

    public async Task<List<string>> GetLocalBranchesAsync(string folderPath)
    {
        var (exitCode, output) = await RunGitAsync(folderPath, "branch", "--format=%(refname:short)");
        if (exitCode != 0) return [];

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrEmpty(b))
            .ToList();
    }

    public async Task<List<string>> GetRemoteBranchesAsync(string folderPath)
    {
        var (exitCode, output) = await RunGitAsync(folderPath, "branch", "-r", "--format=%(refname:short)");
        if (exitCode != 0) return [];

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrEmpty(b) && !b.Contains("HEAD"))
            .ToList();
    }

    public async Task<bool> IsDirtyAsync(string folderPath)
    {
        var (exitCode, output) = await RunGitAsync(folderPath, "status", "--porcelain");
        return exitCode == 0 && !string.IsNullOrWhiteSpace(output);
    }

    public async Task<(bool Success, string Output)> CheckoutAsync(string folderPath, string branchName)
    {
        (int exitCode, string output) result;

        if (branchName.Contains('/'))
        {
            var localName = branchName[(branchName.IndexOf('/') + 1)..];
            result = await RunGitAsync(folderPath, "checkout", "-B", localName, branchName);
        }
        else
        {
            result = await RunGitAsync(folderPath, "checkout", branchName);
        }

        return (result.exitCode == 0, result.output);
    }

    private static async Task<(int ExitCode, string Output)> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync();

        var output = await stdoutTask;
        if (string.IsNullOrEmpty(output))
            output = await stderrTask;

        return (process.ExitCode, output);
    }
}
