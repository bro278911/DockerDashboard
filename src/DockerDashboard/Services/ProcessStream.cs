using System.Diagnostics;
using System.IO;

namespace DockerDashboard.Services;

public sealed class ProcessStream : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    public StreamReader StandardOutput => _process.StandardOutput;
    public StreamReader StandardError => _process.StandardError;

    internal ProcessStream(Process process) => _process = process;

    public void Kill()
    {
        if (_disposed || _process.HasExited) return;
        try { _process.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Kill();
        _process.Dispose();
    }
}
