using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DockerDashboard.Models;
using DockerDashboard.Services;
using Application = System.Windows.Application;

namespace DockerDashboard.ViewModels;

public partial class MainViewModel
{
    private ProcessStream? _logProcess;
    private CancellationTokenSource? _logCts;

    partial void OnLogFilterChanged(string value)
    {
        LogView?.Refresh();
    }

    private bool LogFilterPredicate(object item)
    {
        if (string.IsNullOrWhiteSpace(LogFilter)) return true;
        return item is string line && line.Contains(LogFilter, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSelectedServiceChanged(DockerService? value)
    {
        if (value != null)
            ViewLogs(value);
        else
        {
            StopLogStream();
            StatusMessage = "就緒";
        }
    }

    [RelayCommand]
    private void ViewLogs(DockerService? service)
    {
        if (service == null) return;
        StopLogStream();
        LogLines.Clear();

        var containerName = !string.IsNullOrEmpty(service.ContainerId)
            ? service.ContainerId
            : !string.IsNullOrEmpty(service.ContainerName)
                ? service.ContainerName
                : null;

        _logCts = new CancellationTokenSource();

        _logProcess = !string.IsNullOrEmpty(containerName)
            ? _dockerCli.StartLogStream(containerName)
            : _dockerCli.StartComposeLogStream(service.WorkingDirectory, service.Name);

        Task.Run(() => ReadLogStreamAsync(_logProcess, _logCts.Token));
        StatusMessage = $"正在串流 {service.Name} 的日誌...";
    }

    [RelayCommand]
    private void StopLogs()
    {
        StopLogStream();
        StatusMessage = "日誌串流已停止";
    }

    [RelayCommand]
    private void ClearLogs()
    {
        LogLines.Clear();
    }

    [RelayCommand]
    private async Task ExportLogsAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "匯出日誌",
            Filter = "文字檔 (*.txt)|*.txt|日誌檔 (*.log)|*.log",
            FileName = $"docker-logs-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };

        if (dialog.ShowDialog() != true) return;

        var snapshot = LogLines.ToArray();
        await File.WriteAllLinesAsync(dialog.FileName, snapshot);
        StatusMessage = $"日誌已匯出到 {dialog.FileName}";
    }

    private async Task ReadLogStreamAsync(ProcessStream stream, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(
                ReadStreamAsync(stream.StandardOutput, ct),
                ReadStreamAsync(stream.StandardError, ct));
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReadStreamAsync(System.IO.StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            AppendLog(line);
        }
    }

    internal void StopLogStream()
    {
        _logCts?.Cancel();
        _logProcess?.Dispose();
        _logProcess = null;
        _logCts?.Dispose();
        _logCts = null;
    }
}
