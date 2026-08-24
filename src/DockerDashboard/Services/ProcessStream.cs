using System.Diagnostics;
using System.IO;

namespace DockerDashboard.Services;

public sealed class ProcessStream : IDisposable
{
    private readonly Process _process;
    private readonly Lock _gate = new();
    private bool _disposed;

    public StreamReader StandardOutput => _process.StandardOutput;
    public StreamReader StandardError => _process.StandardError;
    public int ExitCode => _process.ExitCode;

    /// <summary>
    /// 行程是否已結束。已 Dispose 視同結束；查詢失敗時保守回傳 false，
    /// 讓呼叫端把「無法確認」當成「可能還活著」而繼續追蹤，不要誤放生
    /// </summary>
    public bool HasExited
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return true;
                try { return _process.HasExited; }
                catch { return false; }
            }
        }
    }

    /// <summary>行程 PID；已 Dispose 或查詢失敗時回傳 null（僅供診斷訊息使用）</summary>
    public int? Id
    {
        get
        {
            lock (_gate)
            {
                if (_disposed) return null;
                try { return _process.Id; }
                catch { return null; }
            }
        }
    }

    internal ProcessStream(Process process) => _process = process;

    public Task WaitForExitAsync(CancellationToken ct = default) => _process.WaitForExitAsync(ct);

    // 同一個 ProcessStream 可能被多條執行緒同時 Kill/Dispose（例如 ContainerMonitorService.Stop()
    // 與 EventsLoop 的 finally），故整段狀態檢查與 handle 存取都要在鎖內，且對重複呼叫冪等；
    // HasExited 也必須包在 try 內，否則另一邊已 Dispose 時會拋出並逃出呼叫端
    public void Kill()
    {
        lock (_gate)
        {
            KillCore();
        }
    }

    private void KillCore()
    {
        if (_disposed) return;
        try
        {
            if (_process.HasExited) return;
            _process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            // 先終止再標記：順序顛倒的話 KillCore 開頭的 _disposed 檢查會直接 return，
            // 行程不會被終止（docker logs -f、前端 dev server 都會殘留）
            KillCore();
            _disposed = true;
            _process.Dispose();
        }
    }
}
