using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockerDashboard.Models;
using DockerDashboard.Services;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;

namespace DockerDashboard.ViewModels;

public partial class MainViewModel
{
    // dev server / 一次性指令行程追蹤（key 為專案實例；僅 UI 執行緒讀寫）
    private sealed class FrontendProcess
    {
        public required ProcessStream Stream { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public bool UserStopped { get; set; }
        public Task? ReaderTask { get; set; }
    }

    private readonly Dictionary<FrontendProject, FrontendProcess> _devProcesses = [];
    private readonly Dictionary<FrontendProject, FrontendProcess> _oneShotProcesses = [];

    public ObservableCollection<FrontendProject> InternalProjects { get; } = [];
    public ObservableCollection<FrontendProject> ExternalProjects { get; } = [];
    public FrontendLogBuffer InternalLog { get; } = new();
    public FrontendLogBuffer ExternalLog { get; } = new();

    [ObservableProperty]
    private string _internalRunningLabel = "無執行中專案";

    [ObservableProperty]
    private string _externalRunningLabel = "無執行中專案";

    internal ObservableCollection<FrontendProject> ProjectsOf(FrontendGroup group)
        => group == FrontendGroup.Internal ? InternalProjects : ExternalProjects;

    internal static FrontendProject? FindRunningInGroup(
        IEnumerable<FrontendProject> groupProjects, FrontendProject candidate)
        => groupProjects.FirstOrDefault(p => p.IsDevRunning && !ReferenceEquals(p, candidate));

    internal static string BuildRunningLabel(IEnumerable<FrontendProject> groupProjects)
    {
        var running = groupProjects.FirstOrDefault(p => p.IsDevRunning);
        if (running == null) return "無執行中專案";
        var branch = string.IsNullOrEmpty(running.CurrentBranch) ? "非 git" : running.CurrentBranch;
        return $"{running.Name}（{running.FolderName} / {branch}）";
    }

    private void UpdateFrontendRunningLabels()
    {
        InternalRunningLabel = BuildRunningLabel(InternalProjects);
        ExternalRunningLabel = BuildRunningLabel(ExternalProjects);
    }

    internal void AppendFrontendLog(FrontendProject project, string message)
    {
        var buffer = project.Group == FrontendGroup.Internal ? InternalLog : ExternalLog;
        buffer.Append(message);
    }

    [RelayCommand]
    private async Task StartFrontendAsync(FrontendProject? project)
    {
        if (project == null || _devProcesses.ContainsKey(project)) return;

        // 同組互斥：已有執行中專案時先確認再關舊起新
        var running = FindRunningInGroup(ProjectsOf(project.Group), project);
        if (running != null)
        {
            var confirm = System.Windows.MessageBox.Show(
                $"同組已有「{running.Name}」（{running.FolderName} / {running.CurrentBranch}）執行中。\n\n" +
                $"要停止它並啟動「{project.Name}」嗎？",
                "同組互斥確認",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;
            await StopFrontendAsync(running);
        }

        // 啟動前重新讀取分支，確保顯示與實際一致
        if (_gitService.IsGitRepository(project.FolderPath))
            project.CurrentBranch = await _gitService.GetCurrentBranchAsync(project.FolderPath);

        ProcessStream stream;
        try
        {
            stream = _nodeService.Start(project.FolderPath, project.DevCommand);
        }
        catch (Exception ex)
        {
            project.Status = FrontendStatus.Crashed;
            AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ❌ [{project.Name}] 啟動失敗: {ex.Message}");
            StatusMessage = $"❌ {project.Name} 啟動失敗";
            return;
        }

        var ctx = new FrontendProcess { Stream = stream, Cts = new CancellationTokenSource() };
        _devProcesses[project] = ctx;
        project.IsDevRunning = true;
        // 一次性指令正在跑時維持 Busy，否則轉 Running
        if (project.Status != FrontendStatus.Busy)
            project.Status = FrontendStatus.Running;
        AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ▶ [{project.Name}] 啟動 dev server（{project.DevCommand}）於 {project.FolderPath}");
        StatusMessage = $"▶ {project.Name} dev server 啟動中（{project.CurrentBranch}）";
        UpdateFrontendRunningLabels();

        ctx.ReaderTask = Task.Run(() => RunDevProcessAsync(project, ctx));
    }

    private async Task RunDevProcessAsync(FrontendProject project, FrontendProcess ctx)
    {
        try
        {
            await Task.WhenAll(
                ReadFrontendStreamAsync(project, ctx.Stream.StandardOutput, ctx.Cts.Token),
                ReadFrontendStreamAsync(project, ctx.Stream.StandardError, ctx.Cts.Token));
        }
        catch (OperationCanceledException) { }

        int exitCode;
        try
        {
            await ctx.Stream.WaitForExitAsync(CancellationToken.None);
            exitCode = ctx.Stream.ExitCode;
        }
        catch
        {
            exitCode = -1;
        }

        await (Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _devProcesses.Remove(project);
            project.IsDevRunning = false;

            if (ctx.UserStopped)
            {
                if (project.Status != FrontendStatus.Busy)
                    project.Status = FrontendStatus.Stopped;
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ⏹ [{project.Name}] dev server 已停止");
            }
            else
            {
                project.Status = FrontendStatus.Crashed;
                AppendFrontendLog(project, $"[{DateTime.Now:HH:mm:ss}] ⚠️ [{project.Name}] dev server 意外結束（exit code {exitCode}）");
                try
                {
                    _notifyIcon?.ShowBalloonTip(
                        3000,
                        "前端 dev server 異常結束",
                        $"{project.Name}（{project.FolderName}）已停止（exit code {exitCode}）",
                        Forms.ToolTipIcon.Warning);
                }
                catch (ObjectDisposedException) { }
            }

            UpdateFrontendRunningLabels();
            ctx.Stream.Dispose();
            ctx.Cts.Dispose();
        }).Task ?? Task.CompletedTask);
    }

    private async Task ReadFrontendStreamAsync(
        FrontendProject project, System.IO.StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            AppendFrontendLog(project, $"[{project.Name}] {line}");
        }
    }

    [RelayCommand]
    private async Task StopFrontendAsync(FrontendProject? project)
    {
        if (project == null || !_devProcesses.TryGetValue(project, out var ctx)) return;

        ctx.UserStopped = true;
        ctx.Cts.Cancel();
        ctx.Stream.Kill(); // entireProcessTree: true，整樹殺掉 node 子行程
        if (ctx.ReaderTask != null)
            await ctx.ReaderTask; // 等舊行程完全結束再返回，互斥切換時避免 port 尚未釋放
    }

    /// <summary>App 關閉時整樹終止所有前端行程（Dispose 呼叫）</summary>
    internal void StopAllFrontendProcesses()
    {
        foreach (var ctx in _devProcesses.Values.Concat(_oneShotProcesses.Values))
        {
            ctx.UserStopped = true;
            ctx.Cts.Cancel();
            ctx.Stream.Dispose(); // Dispose 內含 Kill(entireProcessTree: true)
            ctx.Cts.Dispose();
        }
        _devProcesses.Clear();
        _oneShotProcesses.Clear();
    }
}
