using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Velopack;
using Forms = System.Windows.Forms;

namespace DockerDashboard.ViewModels;

public partial class MainViewModel
{
    private UpdateInfo? _pendingUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateStatusText))]
    [NotifyPropertyChangedFor(nameof(CanInstallUpdate))]
    [NotifyPropertyChangedFor(nameof(ShowCheckUpdateButton))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    private bool _hasUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateStatusText))]
    private string _updateVersion = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateStatusText))]
    [NotifyPropertyChangedFor(nameof(CanInstallUpdate))]
    [NotifyPropertyChangedFor(nameof(ShowCheckUpdateButton))]
    [NotifyPropertyChangedFor(nameof(CheckUpdateButtonText))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdateCommand))]
    private bool _isDownloadingUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateStatusText))]
    private int _updateProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCheckUpdateButton))]
    [NotifyPropertyChangedFor(nameof(CheckUpdateButtonText))]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdateCommand))]
    private bool _isCheckingUpdate;

    // 「檢查更新」按鈕：無新版本時顯示
    public bool ShowCheckUpdateButton => !HasUpdate && !IsDownloadingUpdate;

    // 按鈕文字：檢查中 or 檢查更新
    public string CheckUpdateButtonText => IsCheckingUpdate ? "檢查中…" : "檢查更新";

    // 「有新版本」按鈕的文字
    public string UpdateStatusText => IsDownloadingUpdate
        ? $"下載中 {UpdateProgress}%"
        : $"有新版本 {UpdateVersion}";

    public bool CanInstallUpdate => HasUpdate && !IsDownloadingUpdate;

    [RelayCommand(CanExecute = nameof(CanCheckUpdate))]
    private async Task CheckUpdateAsync()
    {
        IsCheckingUpdate = true;
        try
        {
            var info = await _updateService.CheckForUpdatesAsync();
            if (info is not null)
            {
                _pendingUpdate = info;
                UpdateVersion = info.TargetFullRelease.Version.ToString();
                HasUpdate = true;
                ShowUpdateBalloonTip("發現新版本", $"版本 {UpdateVersion} 可更新，點擊「有新版本」按鈕下載安裝。");
            }
            else if (_updateService.IsInstalled)
            {
                StatusMessage = "已是最新版本";
                ShowUpdateBalloonTip("已是最新版本", "目前已是最新版本，無需更新。");
            }
            else
            {
                StatusMessage = "開發模式：略過更新檢查";
            }
        }
        catch
        {
            StatusMessage = "無法連線至更新伺服器";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private bool CanCheckUpdate() => !IsCheckingUpdate && !IsDownloadingUpdate;

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is null) return;
        IsDownloadingUpdate = true;
        try
        {
            await _updateService.DownloadUpdateAsync(_pendingUpdate, p => UpdateProgress = p);
            _updateService.ApplyUpdateAndRestart(_pendingUpdate);
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ 更新失敗：{ex.Message}";
            IsDownloadingUpdate = false;
            HasUpdate = false;
            _pendingUpdate = null;
        }
    }

    private void ShowUpdateBalloonTip(string title, string message)
    {
        try
        {
            _notifyIcon?.ShowBalloonTip(4000, title, message, Forms.ToolTipIcon.Info);
        }
        catch (ObjectDisposedException) { }
    }
}
