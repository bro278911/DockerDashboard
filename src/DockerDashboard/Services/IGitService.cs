using System.Collections.Generic;
using System.Threading.Tasks;

namespace DockerDashboard.Services;

/// <summary>
/// PullAsync 的執行結果。BlockedByDirty 為 true 時代表根本沒執行 pull；
/// Restored 只在 pull 失敗時有意義，代表是否已成功還原到 pull 前的 HEAD
/// （含「本來就沒變」與「reset --keep 成功」兩種情況）
/// </summary>
public sealed record GitPullResult(
    bool Success,
    string Output,
    bool BlockedByDirty,
    bool Restored,
    string? RestoreError,
    string? PreviousSha);

public interface IGitService
{
    bool IsGitRepository(string folderPath);
    Task<string> GetCurrentBranchAsync(string folderPath);
    Task<List<string>> GetLocalBranchesAsync(string folderPath);
    Task<List<string>> GetRemoteBranchesAsync(string folderPath);
    Task<bool> IsDirtyAsync(string folderPath);
    Task<(bool Success, string Output)> CheckoutAsync(string folderPath, string branchName);
    Task<GitPullResult> PullAsync(string folderPath, string remoteBranch);
}
