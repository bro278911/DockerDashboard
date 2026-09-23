using System;
using System.Diagnostics;
using System.IO;
using DockerDashboard.Services;

namespace DockerDashboard.Tests;

/// <summary>
/// GitService.PullAsync 的整合測試：用真實暫存 git repo（bare remote + 兩個 clone）
/// 驗證 fast-forward 成功、dirty 擋下、衝突時自動還原三種情境。
/// </summary>
public class GitServicePullTests : IDisposable
{
    private readonly string _root;
    private readonly string _bareDir;
    private readonly string _localDir;
    private readonly string _otherDir;
    private readonly GitService _sut = new();

    public GitServicePullTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "git-pull-test-" + Path.GetRandomFileName());
        _bareDir = Path.Combine(_root, "origin.git");
        _localDir = Path.Combine(_root, "local");
        _otherDir = Path.Combine(_root, "other");
        Directory.CreateDirectory(_root);

        RunGit(_root, "init", "--bare", "-b", "main", _bareDir);

        RunGit(_root, "clone", _bareDir, _localDir);
        ConfigureIdentity(_localDir);
        File.WriteAllText(Path.Combine(_localDir, "a.txt"), "base\n");
        RunGit(_localDir, "add", "a.txt");
        RunGit(_localDir, "commit", "-m", "init");
        RunGit(_localDir, "push", "-u", "origin", "main");

        RunGit(_root, "clone", _bareDir, _otherDir);
        ConfigureIdentity(_otherDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 暫存測試目錄清理失敗不影響測試結果，忽略即可
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task PullAsync_FastForward成功時更新工作樹並回傳成功()
    {
        File.WriteAllText(Path.Combine(_otherDir, "b.txt"), "other change\n");
        RunGit(_otherDir, "add", "b.txt");
        RunGit(_otherDir, "commit", "-m", "other change");
        RunGit(_otherDir, "push", "origin", "main");

        var result = await _sut.PullAsync(_localDir, "origin/main");

        Assert.True(result.Success);
        Assert.False(result.BlockedByDirty);
        Assert.True(File.Exists(Path.Combine(_localDir, "b.txt")));
    }

    [Fact]
    public async System.Threading.Tasks.Task PullAsync_有未提交變更時直接擋下不執行pull()
    {
        File.WriteAllText(Path.Combine(_localDir, "a.txt"), "dirty change\n");

        var result = await _sut.PullAsync(_localDir, "origin/main");

        Assert.False(result.Success);
        Assert.True(result.BlockedByDirty);

        // 沒有推進到 origin 的新 commit，本地內容應維持未提交前的髒改動
        Assert.Equal("dirty change\n", File.ReadAllText(Path.Combine(_localDir, "a.txt")));
    }

    [Fact]
    public async System.Threading.Tasks.Task PullAsync_衝突時自動還原至pull前的HEAD且無殘留MERGE_HEAD()
    {
        File.WriteAllText(Path.Combine(_localDir, "a.txt"), "local change\n");
        RunGit(_localDir, "add", "a.txt");
        RunGit(_localDir, "commit", "-m", "local change");
        var localHeadBeforePull = RunGit(_localDir, "rev-parse", "HEAD").Output.Trim();

        File.WriteAllText(Path.Combine(_otherDir, "a.txt"), "other change\n");
        RunGit(_otherDir, "add", "a.txt");
        RunGit(_otherDir, "commit", "-m", "other change");
        RunGit(_otherDir, "push", "origin", "main");

        var result = await _sut.PullAsync(_localDir, "origin/main");

        Assert.False(result.Success);
        Assert.False(result.BlockedByDirty);
        Assert.True(result.Restored);
        Assert.Null(result.RestoreError);

        var headAfterPull = RunGit(_localDir, "rev-parse", "HEAD").Output.Trim();
        Assert.Equal(localHeadBeforePull, headAfterPull);

        var mergeHeadCheck = RunGit(_localDir, "rev-parse", "-q", "--verify", "MERGE_HEAD");
        Assert.NotEqual(0, mergeHeadCheck.ExitCode);

        var statusOutput = RunGit(_localDir, "status", "--porcelain").Output;
        Assert.True(string.IsNullOrWhiteSpace(statusOutput));
    }

    private static void ConfigureIdentity(string dir)
    {
        RunGit(dir, "config", "user.email", "test@example.com");
        RunGit(dir, "config", "user.name", "Test");
    }

    private static (int ExitCode, string Output) RunGit(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdout + stderr);
    }
}
