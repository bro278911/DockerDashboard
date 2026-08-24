using System.Diagnostics;
using System.IO;

namespace DockerDashboard.Services;

public sealed class ProcessStream : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    public StreamReader StandardOutput => _process.StandardOutput;
    public StreamReader StandardError => _process.StandardError;
    public int ExitCode => _process.ExitCode;

    internal ProcessStream(Process process) => _process = process;

    public Task WaitForExitAsync(CancellationToken ct = default) => _process.WaitForExitAsync(ct);

    public void Kill()
    {
        if (_disposed || _process.HasExited) return;
        try { _process.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // 先 Kill 再設 _disposed：順序顛倒的話 Kill() 開頭的 _disposed 檢查會直接 return，
        // 行程不會被終止（docker logs -f、前端 dev server 都會殘留）
        Kill();
        _disposed = true;
        _process.Dispose();
    }
}
