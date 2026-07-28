using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    // 砍掉佔 host port 80 的非-docker 進程（wslrelay 等），解除與 nginx 的埠衝突
    private async void ReleasePort80_Click(object sender, RoutedEventArgs e)
    {
        SetRunning(true);
        _log.Clear();
        LogText.Text = string.Empty;
        StatusLabel.Text = "釋放 Port 80";
        try
        {
            AppendLog("▶ 掃描 host port 80 佔用...");
            var listeners = await Task.Run(FindPort80Listeners);
            if (listeners.Count == 0)
            {
                AppendLog("ℹ Port 80 沒有 LISTENING 佔用");
                return;
            }

            var killed = 0;
            foreach (var (pid, addr) in listeners)
            {
                string name;
                try { name = Process.GetProcessById(pid).ProcessName; }
                catch { continue; }

                // docker 自己的 listener 不砍
                if (name.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("vpnkit", StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog($"  跳過 docker 自身：{name} (PID {pid}, {addr})");
                    continue;
                }

                try
                {
                    Process.GetProcessById(pid).Kill(entireProcessTree: true);
                    AppendLog($"  ✅ 已砍：{name} (PID {pid}, {addr})");
                    killed++;
                }
                catch (Exception ex)
                {
                    AppendLog($"  ⚠ {name} (PID {pid}) 砍失敗：{ex.Message}");
                }
            }

            AppendLog(string.Empty);
            AppendLog(killed > 0
                ? $"✅ 已釋放 Port 80，砍掉 {killed} 個非-docker 佔用進程。localhost 現在應該走 docker。"
                : "ℹ 沒有可砍的非-docker 佔用（都是 docker 自身）");
            StatusLabel.Text = "完成";
        }
        catch (Exception ex)
        {
            AppendLog($"❌ 例外：{ex.Message}");
            StatusLabel.Text = "發生錯誤";
        }
        finally
        {
            SetRunning(false);
        }
    }

    // 解析 netstat，回傳 LISTENING 在 port 80 的 (PID, 本機位址)；跳過 System/Idle(0,4)
    private static List<(int Pid, string Address)> FindPort80Listeners()
    {
        var result = new List<(int, string)>();
        var seen = new HashSet<int>();
        var psi = new ProcessStartInfo
        {
            FileName = "netstat",
            Arguments = "-ano -p TCP",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return result;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        foreach (var line in output.Split('\n'))
        {
            var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 5 || t[0] != "TCP" || t[3] != "LISTENING") continue;
            if (!t[1].EndsWith(":80", StringComparison.Ordinal)) continue;
            if (!int.TryParse(t[4], out var pid) || pid <= 4) continue;
            if (seen.Add(pid)) result.Add((pid, t[1]));
        }
        return result;
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
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
            ReleasePort80Btn.IsEnabled = !running;
            RepairProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            PruneDanglingCheck.IsEnabled = !running;
            PruneAllImagesCheck.IsEnabled = !running;
            PruneVolumesCheck.IsEnabled = !running;
            SystemPruneCheck.IsEnabled = !running;
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
