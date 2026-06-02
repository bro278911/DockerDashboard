using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DockerDashboard.Services;

namespace DockerDashboard.Views;

public partial class DockerRepairWindow : Window
{
    private readonly IDockerCliService _dockerCli;
    private CancellationTokenSource? _cts;
    private readonly StringBuilder _log = new();

    public DockerRepairWindow(IDockerCliService dockerCli)
    {
        InitializeComponent();
        _dockerCli = dockerCli;
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        if (!PruneDanglingCheck.IsChecked == true &&
            !PruneAllImagesCheck.IsChecked == true &&
            !PruneVolumesCheck.IsChecked == true &&
            !SystemPruneCheck.IsChecked == true)
        {
            AppendLog("⚠ 請至少選擇一個修復項目");
            return;
        }

        if (SystemPruneCheck.IsChecked == true)
        {
            var confirm = System.Windows.MessageBox.Show(
                "「完整系統清除」將移除所有 image、volume 和 network。\n\n此操作無法還原，確定繼續？",
                "高風險操作確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != System.Windows.MessageBoxResult.Yes) return;
        }

        SetRunning(true);
        _log.Clear();
        LogText.Text = string.Empty;

        _cts = new CancellationTokenSource();

        try
        {
            if (PruneDanglingCheck.IsChecked == true)
                await RunStep("清除懸空 Image", () =>
                    _dockerCli.DockerImagePruneAsync(false, AppendLog, _cts.Token));

            if (PruneAllImagesCheck.IsChecked == true)
                await RunStep("清除所有未使用 Image", () =>
                    _dockerCli.DockerImagePruneAsync(true, AppendLog, _cts.Token));

            if (PruneVolumesCheck.IsChecked == true)
                await RunStep("清除未使用 Volume", () =>
                    _dockerCli.DockerVolumePruneAsync(AppendLog, _cts.Token));

            if (SystemPruneCheck.IsChecked == true)
                await RunStep("完整系統清除", () =>
                    _dockerCli.DockerSystemPruneAsync(true, true, AppendLog, _cts.Token));

            AppendLog(string.Empty);
            AppendLog("✅ 所有修復步驟完成");
            StatusLabel.Text = "完成";
        }
        catch (OperationCanceledException)
        {
            AppendLog("⚠ 操作已取消");
            StatusLabel.Text = "已取消";
        }
        catch (Exception ex)
        {
            AppendLog($"❌ 例外：{ex.Message}");
            StatusLabel.Text = "發生錯誤";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetRunning(false);
        }
    }

    private async Task RunStep(string label, Func<Task<(int ExitCode, string Output)>> action)
    {
        AppendLog($"▶ {label}...");
        StatusLabel.Text = label;
        var (exitCode, _) = await action();
        AppendLog(exitCode == 0 ? $"✅ {label} 完成" : $"❌ {label} 失敗（exit {exitCode}）");
        AppendLog(string.Empty);
    }

    private void AppendLog(string line)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _log.AppendLine(line);
            LogText.Text = _log.ToString();
            LogScroll.ScrollToEnd();
        });
    }

    private void SetRunning(bool running)
    {
        Dispatcher.InvokeAsync(() =>
        {
            RepairBtn.IsEnabled = !running;
            RepairProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            PruneDanglingCheck.IsEnabled = !running;
            PruneAllImagesCheck.IsEnabled = !running;
            PruneVolumesCheck.IsEnabled = !running;
            SystemPruneCheck.IsEnabled = !running;
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }
}
