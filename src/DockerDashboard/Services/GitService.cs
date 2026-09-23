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

    public async Task<GitPullResult> PullAsync(string folderPath, string remoteBranch)
    {
        if (await IsDirtyAsync(folderPath))
            return new GitPullResult(false, string.Empty, BlockedByDirty: true, Restored: false, RestoreError: null, PreviousSha: null);

        var slashIndex = remoteBranch.IndexOf('/');
        var remote = slashIndex > 0 ? remoteBranch[..slashIndex] : "origin";
        var branch = slashIndex > 0 ? remoteBranch[(slashIndex + 1)..] : remoteBranch;

        var originalSha = (await RunGitAsync(folderPath, "rev-parse", "HEAD")).Output.Trim();

        var pullResult = await RunGitAsync(folderPath, "pull", "--no-edit", remote, branch);
        if (pullResult.ExitCode == 0)
            return new GitPullResult(true, pullResult.Output, false, false, null, null);

        // pull 失敗，嘗試還原到 pull 前的狀態
        string? restoreError = null;

        var mergeHeadCheck = await RunGitAsync(folderPath, "rev-parse", "-q", "--verify", "MERGE_HEAD");
        if (mergeHeadCheck.ExitCode == 0)
        {
            var abortResult = await RunGitAsync(folderPath, "merge", "--abort");
            if (abortResult.ExitCode != 0)
                restoreError = $"merge --abort 失敗: {abortResult.Output}";
        }

        var restored = false;
        if (restoreError == null)
        {
            var currentSha = (await RunGitAsync(folderPath, "rev-parse", "HEAD")).Output.Trim();
            if (currentSha == originalSha)
            {
                restored = true;
            }
            else
            {
                var resetResult = await RunGitAsync(folderPath, "reset", "--keep", originalSha);
                if (resetResult.ExitCode == 0)
                    restored = true;
                else
                    restoreError = $"reset --keep 失敗: {resetResult.Output}";
            }
        }

        return new GitPullResult(false, pullResult.Output, false, restored, restoreError, restored ? originalSha : null);
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

        using var process = ProcessLauncher.Start(psi);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync();

        var output = await stdoutTask;
        var error = await stderrTask;
        if (string.IsNullOrEmpty(output))
            output = error;
        // 失敗時 stdout 常只有進度文字，真正原因在 stderr，一併附上
        else if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
            output = $"{output.TrimEnd()}\n{error}";

        return (process.ExitCode, output);
    }
}
