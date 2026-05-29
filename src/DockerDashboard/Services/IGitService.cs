using System.Collections.Generic;
using System.Threading.Tasks;

namespace DockerDashboard.Services;

public interface IGitService
{
    bool IsGitRepository(string folderPath);
    Task<string> GetCurrentBranchAsync(string folderPath);
    Task<List<string>> GetLocalBranchesAsync(string folderPath);
    Task<List<string>> GetRemoteBranchesAsync(string folderPath);
    Task<bool> IsDirtyAsync(string folderPath);
    Task<(bool Success, string Output)> CheckoutAsync(string folderPath, string branchName);
}
