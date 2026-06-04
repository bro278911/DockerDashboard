using System.Diagnostics;
using System.IO;
using Velopack;
using Velopack.Sources;

namespace DockerDashboard.Services;

public class UpdateService
{
    private const string RepoUrl = "https://github.com/bro278911/DockerDashboard";
    private readonly UpdateManager _manager;

    public UpdateService()
    {
        var source = new GithubSource(RepoUrl, null, false);
        _manager = new UpdateManager(source);
    }

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? "開發版";

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        if (!IsInstalled) return null;
        return await _manager.CheckForUpdatesAsync();
    }

    public async Task DownloadUpdateAsync(UpdateInfo info, Action<int>? progress = null)
        => await _manager.DownloadUpdatesAsync(info, progress);

    public void ApplyUpdateAndRestart(UpdateInfo info)
    {
        // Velopack 的 --waitPid 在某些 Windows 設定下會 OpenProcess(SYNCHRONIZE) 失敗，
        // 導致 Update.exe 不等 app 結束就嘗試替換檔案。
        // 另外 UseShellExecute = false 會讓子 process 繼承 file handle，造成檔案永遠被 lock。
        // 修法：UseShellExecute = true（不繼承 handle）+ 延長等待讓 AV 掃描完成。
        var installRoot = TryGetInstallRoot();
        var updateExe = Path.Combine(installRoot, "Update.exe");
        var nupkgPath = Path.Combine(installRoot, "packages",
            $"DockerDashboard-{info.TargetFullRelease.Version}-full.nupkg");

        if (!File.Exists(updateExe) || !File.Exists(nupkgPath))
        {
            _manager.ApplyUpdatesAndRestart(info);
            return;
        }

        // UseShellExecute = true：不繼承 file handle
        // timeout /t 20：等 app 完全退出 + AV 掃描（最多 20 秒）
        var cmdArgs = $"/c timeout /t 20 /nobreak > nul & \"{updateExe}\" apply -p \"{nupkgPath}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmdArgs)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        // 用 WPF 正常 Shutdown 而非 Environment.Exit，讓 CLR 乾淨釋放所有 file handle
        System.Windows.Application.Current.Dispatcher.Invoke(
            () => System.Windows.Application.Current.Shutdown());
    }

    private static string TryGetInstallRoot()
    {
        // current\ 的上一層是安裝根目錄
        var processDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        var parent = Path.GetDirectoryName(processDir) ?? "";
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            return parent;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DockerDashboard");
    }
}
