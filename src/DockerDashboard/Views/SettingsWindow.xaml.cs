using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DockerDashboard.Models;
using DockerDashboard.Services;

namespace DockerDashboard.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        PollSlider.Value = settings.PollIntervalSeconds;
        ComposeV2Toggle.IsChecked = settings.UseComposeV2;
        WslDistroBox.Text = settings.WslDistroName;
        AutoWatchToggle.IsChecked = settings.AutoWatchEnabled;
        WatchDebounceSlider.Value = settings.WatchDebounceSeconds;
        BuildKitSlider.Value = settings.BuildKitParallelism;

        if (settings.DockerMode == DockerMode.Wsl2)
            ModeWsl2.IsChecked = true;
        else
            ModeDockerDesktop.IsChecked = true;

        UpdateWsl2PanelVisibility();
    }

    private void DockerMode_Changed(object sender, RoutedEventArgs e)
    {
        UpdateWsl2PanelVisibility();
    }

    private void UpdateWsl2PanelVisibility()
    {
        if (Wsl2Panel != null)
            Wsl2Panel.Visibility = ModeWsl2.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var distro = WslDistroBox.Text.Trim();
        if (string.IsNullOrEmpty(distro))
        {
            TestResultText.Text = "❌ 請輸入 WSL 發行版名稱";
            TestResultText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            return;
        }

        TestResultText.Text = "⏳ 測試中...";
        TestResultText.Foreground = System.Windows.Media.Brushes.Gray;
        TestConnectionBtn.IsEnabled = false;

        try
        {
            var (wslOk, wslMsg) = await TestWslAsync(distro);
            if (!wslOk)
            {
                TestResultText.Text = $"❌ WSL 發行版 '{distro}' 不可用：{wslMsg}";
                TestResultText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                return;
            }

            // WSL 剛啟動時 [boot] 的 service docker start 需要幾秒才完成，
            // 最多等 12 秒（每次 1.5 秒，共 8 次）再回報失敗
            const int maxAttempts = 8;
            bool dockerOk = false;
            string dockerMsg = string.Empty;

            for (int i = 0; i < maxAttempts; i++)
            {
                if (i > 0)
                {
                    TestResultText.Text = $"⏳ 等待 Docker 啟動… ({i}/{maxAttempts - 1})";
                    await Task.Delay(1500);
                }

                (dockerOk, dockerMsg) = await TestDockerInWslAsync(distro);
                if (dockerOk) break;
            }

            if (!dockerOk)
            {
                var hint = dockerMsg.Contains("connect", StringComparison.OrdinalIgnoreCase)
                    ? $"❌ Docker daemon 未啟動：{dockerMsg}\n💡 請在 Ubuntu 終端執行：sudo service docker start"
                    : $"❌ WSL2 中 Docker 不可用：{dockerMsg}";
                TestResultText.Text = hint;
                TestResultText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                return;
            }

            TestResultText.Text = $"✅ 連線成功！Docker {dockerMsg}";
            TestResultText.Foreground = System.Windows.Media.Brushes.Green;
        }
        finally
        {
            TestConnectionBtn.IsEnabled = true;
        }
    }

    private static async Task<(bool, string)> TestWslAsync(string distro)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(distro);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("echo");
            psi.ArgumentList.Add("ok");

            using var process = new Process { StartInfo = psi };
            using var cts = new CancellationTokenSource(5000);
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            return process.ExitCode == 0 ? (true, output.Trim()) : (false, "發行版無法啟動");
        }
        catch
        {
            return (false, "WSL 未安裝或不可用");
        }
    }

    private static async Task<(bool, string)> TestDockerInWslAsync(string distro)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(distro);
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("docker");
            psi.ArgumentList.Add("version");
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("{{.Server.Version}}");

            using var process = new Process { StartInfo = psi };
            using var cts = new CancellationTokenSource(10000);
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var error = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                return (true, output.Trim());

            return (false, string.IsNullOrEmpty(error) ? "docker 未安裝" : error.Trim());
        }
        catch
        {
            return (false, "無法執行 docker 命令");
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.PollIntervalSeconds = (int)PollSlider.Value;
        _settings.UseComposeV2 = ComposeV2Toggle.IsChecked == true;
        _settings.DockerMode = ModeWsl2.IsChecked == true ? DockerMode.Wsl2 : DockerMode.DockerDesktop;
        _settings.WslDistroName = WslDistroBox.Text.Trim();
        _settings.AutoWatchEnabled = AutoWatchToggle.IsChecked == true;
        _settings.WatchDebounceSeconds = (int)WatchDebounceSlider.Value;
        _settings.BuildKitParallelism = Math.Clamp((int)BuildKitSlider.Value, 0, 8);

        if (string.IsNullOrEmpty(_settings.WslDistroName))
            _settings.WslDistroName = "Ubuntu";

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OpenWsl2Guide_Click(object sender, RoutedEventArgs e)
    {
        var guide = new Wsl2GuideWindow { Owner = this };
        guide.ShowDialog();
    }
}
