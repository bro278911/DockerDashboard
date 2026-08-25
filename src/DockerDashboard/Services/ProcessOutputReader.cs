using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DockerDashboard.Services;

/// <summary>
/// 行程輸出讀取的共用實作。前端 dev server／一次性指令與後端 docker log 串流共用同一套
/// 取消與結束語意，避免兩邊各自維護、修正只套用到其中一條路徑
/// </summary>
public static class ProcessOutputReader
{
    /// <summary>同時讀取 stdout 與 stderr 直到兩者結束或被取消；取消不視為錯誤</summary>
    public static async Task ReadAllAsync(ProcessStream stream, Action<string> onLine, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(
                ReadAsync(stream.StandardOutput, onLine, ct),
                ReadAsync(stream.StandardError, onLine, ct));
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task ReadAsync(StreamReader reader, Action<string> onLine, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            onLine(line);
        }
    }
}
