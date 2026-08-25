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
        {
            try
            {
                RaiseState(project, FrontendProcessKind.Dev, FrontendProcessState.Running, 0, started.Label, false);
            }
            catch (Exception ex)
            {
                // 與 MonitorAsync 尾端同一原則：訂閱者拋出的例外不可讓 StartDevAsync 的
                // Task<StartResult> 跟著失敗，manager 不對訂閱者負責，吞掉並記錄即可
                System.Diagnostics.Debug.WriteLine(
                    $"[FrontendProcessManager] StateChanged 訂閱者拋出例外: {ex.Message}");
            }

            started.Monitor = Task.Run(() => MonitorAsync(project, FrontendProcessKind.Dev, started));
        }

        return Task.FromResult(result);
    }

    public Task<bool> StopDevAsync(FrontendProject project) => StopAsync(project, FrontendProcessKind.Dev);

    public bool IsOneShotActive(FrontendProject project) => IsActive(project, FrontendProcessKind.OneShot);

    public Task<StartResult> RunOneShotAsync(FrontendProject project, string command, string label)
    {
        StartResult result;
        Entry? started;

        lock (_gate)
        {
            if (_entries.ContainsKey((project, FrontendProcessKind.OneShot)))
                return Task.FromResult<StartResult>(new StartResult.AlreadyRunning());

            result = StartCore(project, FrontendProcessKind.OneShot, command, label, out started);
        }

        // 與 StartDevAsync 同一原則：Running 事件必須在離開鎖之後才觸發
        if (result is StartResult.Started && started != null)
        {
            try
            {
                RaiseState(project, FrontendProcessKind.OneShot, FrontendProcessState.Running, 0, started.Label, false);
            }
            catch (Exception ex)
            {
                // 訂閱者拋出的例外不可讓 RunOneShotAsync 的 Task<StartResult> 跟著失敗，
                // manager 不對訂閱者負責，吞掉並記錄即可
                System.Diagnostics.Debug.WriteLine(
                    $"[FrontendProcessManager] StateChanged 訂閱者拋出例外: {ex.Message}");
            }

            started.Monitor = Task.Run(() => MonitorAsync(project, FrontendProcessKind.OneShot, started));
        }

        return Task.FromResult(result);
    }

    public Task<bool> CancelOneShotAsync(FrontendProject project) => StopAsync(project, FrontendProcessKind.OneShot);

    /// <summary>
    /// App 關閉時呼叫（UI 執行緒）。只送出終止請求不等待：等待會與監控任務的收尾互卡；
    /// 未及時退出的行程由 ProcessLauncher 的 Job Object 在 App 行程結束時連帶回收
    /// </summary>
    public void StopAll()
    {
        Entry[] entries;
        lock (_gate)
        {
            entries = [.. _entries.Values];
            _entries.Clear();
        }

        foreach (var entry in entries)
        {
            entry.UserStopped = true;
            try
            {
                entry.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 可能與 MonitorAsync 收尾競態：Cts 已由另一執行緒釋放，視同已取消
            }
            entry.Stream.Dispose(); // 內含 Kill(entireProcessTree: true)
            entry.Cts.Dispose();
        }
    }

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

        try
        {
            entry.Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 同一瞬間行程剛好自然結束：MonitorAsync 已在鎖外把這個 entry 的 Cts Dispose 掉，
            // 此時取消已無意義（行程已經在結束了），視同取消成功繼續往下走 Kill/等待
        }

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
            await ProcessOutputReader.ReadAllAsync(
                entry.Stream,
                line =>
                {
                    // 與 StateChanged 同一原則：訂閱者拋例外不可讓這個讀取迴圈中斷，否則該行程的
                    // log 會靜默停止更新，但狀態機仍顯示行程在跑，是最難查的失敗模式。唯一訂閱者
                    // 最終會呼叫 Dispatcher.InvokeAsync，dispatcher 關閉期間該呼叫可能拋例外，
                    // 不屬於「不可能發生的情境」
                    try { OutputReceived?.Invoke(this, new FrontendOutputEventArgs(project, line)); }
                    catch { /* 吞掉，manager 不對訂閱者負責 */ }
                },
                entry.Cts.Token);
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

        try
        {
            RaiseState(project, kind, state, exitCode, entry.Label, entry.UserStopped);
        }
        catch (Exception ex)
        {
            // 訂閱者拋出的例外不可讓下方的資源釋放被跳過，也不可讓這個 fire-and-forget 的
            // Task.Run 變成 unobserved faulted task；此處吞掉並記錄即可，manager 不對訂閱者負責
            System.Diagnostics.Debug.WriteLine(
                $"[FrontendProcessManager] StateChanged 訂閱者拋出例外: {ex.Message}");
        }
        finally
        {
            entry.Stream.Dispose();
            entry.Cts.Dispose();
        }
    }

    private void RaiseState(
        FrontendProject project, FrontendProcessKind kind, FrontendProcessState state, int exitCode, string label,
        bool userStopped)
        => StateChanged?.Invoke(this, new FrontendStateEventArgs(project, kind, state, exitCode, label, userStopped));
}
