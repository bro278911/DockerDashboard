using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DockerDashboard.Models;

namespace DockerDashboard.Services;

/// <summary>
/// 前端行程生命週期的唯一擁有者。無 UI 相依（不碰 Dispatcher / MessageBox / StatusMessage），
/// 所有狀態轉換集中於此，可用真實行程做單元測試。
///
/// 行程回收由 ProcessLauncher 的 Job Object 保底，因此這裡不需要重試殺行程、
/// 不需要「殺不掉就保留追蹤」之類的補償邏輯。
/// </summary>
public sealed class FrontendProcessManager(NodeProcessService nodeService)
{
    private sealed class Entry
    {
        public required ProcessStream Stream { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required string Label { get; init; }
        public bool UserStopped { get; set; }
        public Task? Monitor { get; set; }
    }

    private readonly NodeProcessService _nodeService = nodeService;
    private readonly Dictionary<(FrontendProject Project, FrontendProcessKind Kind), Entry> _entries = [];
    private readonly Lock _gate = new();

    public event EventHandler<FrontendOutputEventArgs>? OutputReceived;
    public event EventHandler<FrontendStateEventArgs>? StateChanged;

    public bool IsDevActive(FrontendProject project) => IsActive(project, FrontendProcessKind.Dev);

    private bool IsActive(FrontendProject project, FrontendProcessKind kind)
    {
        lock (_gate) return _entries.ContainsKey((project, kind));
    }

    public Task<StartResult> StartDevAsync(
        FrontendProject project, IReadOnlyCollection<FrontendProject> sameGroup)
    {
        StartResult result;
        Entry? started;

        lock (_gate)
        {
            if (_entries.ContainsKey((project, FrontendProcessKind.Dev)))
                return Task.FromResult<StartResult>(new StartResult.AlreadyRunning());

            // 同組互斥：只要同組另一個專案有 dev 追蹤中（含啟動中）就佔用
            var occupant = sameGroup.FirstOrDefault(p =>
                !ReferenceEquals(p, project) && _entries.ContainsKey((p, FrontendProcessKind.Dev)));
            if (occupant != null)
                return Task.FromResult<StartResult>(new StartResult.GroupOccupied(occupant));

            result = StartCore(project, FrontendProcessKind.Dev, project.DevCommand, "dev server", out started);
        }

        // Running 事件必須在離開鎖之後才觸發：鎖內只做「判斷互斥＋啟動行程＋登記追蹤」等內部
        // 狀態轉換，不能假設外部訂閱者不會阻塞或反過來呼叫本 manager（例如同步取用 IsDevActive），
        // 否則在鎖內同步呼叫訂閱者會有死鎖風險
        if (result is StartResult.Started && started != null)
            RaiseState(project, FrontendProcessKind.Dev, FrontendProcessState.Running, 0, started.Label);

        return Task.FromResult(result);
    }

    public Task<bool> StopDevAsync(FrontendProject project) => StopAsync(project, FrontendProcessKind.Dev);

    // 呼叫端必須已持有 _gate；不在此處觸發事件，交由呼叫端在離開鎖之後處理
    private StartResult StartCore(
        FrontendProject project, FrontendProcessKind kind, string command, string label, out Entry? started)
    {
        started = null;

        ProcessStream stream;
        try
        {
            stream = _nodeService.Start(project.FolderPath, command);
        }
        catch (Exception ex)
        {
            return new StartResult.Failed(ex.Message);
        }

        var entry = new Entry
        {
            Stream = stream,
            Cts = new CancellationTokenSource(),
            Label = label,
        };
        _entries[(project, kind)] = entry;
        entry.Monitor = Task.Run(() => MonitorAsync(project, kind, entry));

        started = entry;
        return new StartResult.Started();
    }

    private async Task<bool> StopAsync(FrontendProject project, FrontendProcessKind kind)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue((project, kind), out entry)) return true;
            entry.UserStopped = true;
        }

        entry.Cts.Cancel();
        entry.Stream.Kill();

        if (entry.Monitor != null)
        {
            try { await entry.Monitor.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch { /* 逾時或例外都以下方 HasExited 為準 */ }
        }

        return entry.Stream.HasExited;
    }

    private async Task MonitorAsync(FrontendProject project, FrontendProcessKind kind, Entry entry)
    {
        var exitCode = -1;
        try
        {
            await Task.WhenAll(
                ReadAsync(project, entry.Stream.StandardOutput, entry.Cts.Token),
                ReadAsync(project, entry.Stream.StandardError, entry.Cts.Token));
        }
        catch { /* 讀取結束或被取消，交由下方等待行程結束 */ }

        try
        {
            await entry.Stream.WaitForExitAsync(CancellationToken.None);
            exitCode = entry.Stream.ExitCode;
        }
        catch { exitCode = -1; }

        lock (_gate)
        {
            // 只移除仍指向這個 entry 的項目，避免誤刪之後重新註冊的新行程
            if (_entries.TryGetValue((project, kind), out var current) && ReferenceEquals(current, entry))
                _entries.Remove((project, kind));
        }

        // dev server 的正常狀態是持續執行，只要不是使用者主動停止就算異常結束（不看 exit code，
        // 呼應既有 MainViewModel 崩潰通知邏輯）；一次性指令（install/vitest/e2e）非零結束是
        // 正常結果（測試失敗），不算崩潰
        var state = kind == FrontendProcessKind.Dev
            ? (entry.UserStopped ? FrontendProcessState.Stopped : FrontendProcessState.Crashed)
            : FrontendProcessState.Stopped;

        RaiseState(project, kind, state, exitCode, entry.Label);

        entry.Stream.Dispose();
        entry.Cts.Dispose();
    }

    private async Task ReadAsync(FrontendProject project, System.IO.StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            OutputReceived?.Invoke(this, new FrontendOutputEventArgs(project, line));
        }
    }

    private void RaiseState(
        FrontendProject project, FrontendProcessKind kind, FrontendProcessState state, int exitCode, string label)
        => StateChanged?.Invoke(this, new FrontendStateEventArgs(project, kind, state, exitCode, label));
}
