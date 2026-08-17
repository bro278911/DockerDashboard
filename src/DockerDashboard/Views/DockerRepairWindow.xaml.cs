using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
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
    private static readonly HttpClient PortTestClient = new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

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

    private async void ReleasePort_Click(object sender, RoutedEventArgs e)
    {
        var candidates = new List<(Process Process, string Name, PortListener Listener)>();
        SetRunning(true);
        _log.Clear();
        LogText.Text = string.Empty;
        StatusLabel.Text = "釋放 Port";
        try
        {
            if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
            {
                AppendLog("⚠ 請輸入 1 到 65535 之間的有效 Port。");
                StatusLabel.Text = "Port 無效";
                return;
            }

            AppendLog($"▶ 掃描主機 Port {port} 的監聽程序...");
            var listeners = await Task.Run(() => PortListenerScanner.Scan(port));
            if (listeners.Count == 0)
            {
                AppendLog($"ℹ Port {port} 沒有 LISTENING 中的監聽程序。");
                StatusLabel.Text = "完成";
                return;
            }

            foreach (var listener in listeners)
            {
                Process? process = null;
                string name;
                try
                {
                    process = Process.GetProcessById(listener.Pid);
                    if (process.HasExited)
                    {
                        process.Dispose();
                        AppendLog($"  跳過已結束的程序：PID {listener.Pid} ({listener.Address})");
                        continue;
                    }

                    name = process.ProcessName;
                }
                catch
                {
                    process?.Dispose();
                    AppendLog($"  跳過已不存在的程序：PID {listener.Pid} ({listener.Address})");
                    continue;
                }

                if (name.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("vpnkit", StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog($"  跳過 Docker 自身：{name} (PID {listener.Pid}, {listener.Address})");
                    process.Dispose();
                    continue;
                }

                candidates.Add((process, name, listener));
            }

            if (candidates.Count == 0)
            {
                AppendLog("ℹ 所有監聽程序都屬於 Docker，沒有需要終止的程序。");
                StatusLabel.Text = "完成";
                return;
            }

            var candidateLines = string.Join(Environment.NewLine, candidates.Select(candidate =>
                $"{candidate.Name} (PID {candidate.Listener.Pid}, {candidate.Listener.Address})"));
            var confirmation = System.Windows.MessageBox.Show(
                $"下列非 Docker 程序正在佔用主機 Port {port}：{Environment.NewLine}{Environment.NewLine}" +
                $"{candidateLines}{Environment.NewLine}{Environment.NewLine}是否要終止這些程序？",
                $"確認釋放 Port {port}",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirmation != System.Windows.MessageBoxResult.Yes)
            {
                AppendLog("ℹ 已取消釋放 Port 操作。");
                StatusLabel.Text = "已取消";
                return;
            }

            var killed = 0;
            foreach (var candidate in candidates)
            {
                try
                {
                    candidate.Process.Kill(entireProcessTree: true);
                    AppendLog($"  ✅ 已終止：{candidate.Name} (PID {candidate.Listener.Pid}, {candidate.Listener.Address})");
                    killed++;
                }
                catch (Exception ex)
                {
                    AppendLog($"  ⚠ {candidate.Name} (PID {candidate.Listener.Pid}) 終止失敗：{ex.Message}");
                }
            }

            AppendLog($"共終止 {killed}/{candidates.Count} 個程序。");
            AppendLog(string.Empty);
            AppendLog($"▶ 重新測試 Port {port} 的 HTTP 連線（僅測試 HTTP，不含 HTTPS）...");
            var urls = new[]
            {
                $"http://127.0.0.1:{port}/",
                $"http://localhost:{port}/"
            };
            var allReachable = true;
            foreach (var url in urls)
            {
                try
                {
                    using var response = await PortTestClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    AppendLog($"  ✅ {url} 可連線（HTTP {(int)response.StatusCode}）");
                }
                catch (Exception ex)
                {
                    allReachable = false;
                    AppendLog($"  ❌ {url} 無法連線：{ex.Message}");
                }
            }

            AppendLog(allReachable
                ? $"✅ Port {port} 現在可透過 127.0.0.1 與 localhost 正常回應。"
                : $"⚠ Port {port} 仍有網址無法回應。若目前沒有服務監聽此 Port，無法連線屬正常；" +
                  "若服務已啟動仍連不上，請查看上方測試結果。");
            StatusLabel.Text = "完成";
        }
        catch (Exception ex)
        {
            AppendLog($"❌ 例外：{ex.Message}");
            StatusLabel.Text = "發生錯誤";
        }
        finally
        {
            foreach (var candidate in candidates)
            {
                candidate.Process.Dispose();
            }

            SetRunning(false);
        }
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
            ReleasePortBtn.IsEnabled = !running;
            PortBox.IsEnabled = !running;
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
