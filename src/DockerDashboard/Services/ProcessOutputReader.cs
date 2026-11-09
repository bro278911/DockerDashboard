using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;

namespace DockerDashboard.Services;

/// <summary>
/// 行程輸出讀取的共用實作。前端 dev server／一次性指令與後端 docker log 串流共用同一套
/// 取消與結束語意，避免兩邊各自維護、修正只套用到其中一條路徑
/// </summary>
public static class ProcessOutputReader
{
    // cmd.exe 自身的訊息（例如指令打錯的「不是內部或外部命令」）在 redirect 情境下一律走系統
    // OEM codepage，不受 chcp 與 StandardErrorEncoding 影響，而 npm/vite 的輸出是 UTF-8，兩者會
    // 混在同一條 stderr。因此改以 byte 讀取、逐行先嚴格 UTF-8 解碼，失敗才退回 OEM。
    // cp950 位元組恰好構成合法 UTF-8 的機率極低，視為可接受的誤判風險。
    internal static readonly Encoding OemEncoding = CreateOemEncoding();

    private static Encoding CreateOemEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(GetOEMCP());
    }

    [DllImport("kernel32.dll")]
    private static extern int GetOEMCP();

    /// <summary>同時讀取 stdout 與 stderr 直到兩者結束或被取消；取消不視為錯誤</summary>
    public static async Task ReadAllAsync(ProcessStream stream, Action<string> onLine, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(
                ReadLinesAsync(stream.StandardOutput.BaseStream, OemEncoding, onLine, ct),
                ReadLinesAsync(stream.StandardError.BaseStream, OemEncoding, onLine, ct));
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>以 byte 切行（\r\n、\r、\n 語意同 StreamReader.ReadLine），每行獨立決定編碼：合法 UTF-8 即 UTF-8，否則用 fallback</summary>
    internal static async Task ReadLinesAsync(Stream stream, Encoding fallback, Action<string> onLine, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var line = new MemoryStream();
        // 上一個 buffer 以 \r 結尾時，下一個 buffer 開頭的 \n 屬於同一個 \r\n，不得多算一行
        var pendingCr = false;

        while (!ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;

            var start = 0;
            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (pendingCr)
                {
                    pendingCr = false;
                    if (b == (byte)'\n')
                    {
                        start = i + 1;
                        continue;
                    }
                }
                if (b != (byte)'\r' && b != (byte)'\n') continue;

                line.Write(buffer, start, i - start);
                // 切換日誌時舊串流已被取消，緩衝內尚未輸出的行不得再灌進新日誌
                if (ct.IsCancellationRequested) return;
                Emit(line, fallback, onLine);
                start = i + 1;
                pendingCr = b == (byte)'\r';
            }
            line.Write(buffer, start, read - start);
        }

        if (line.Length > 0 && !ct.IsCancellationRequested) Emit(line, fallback, onLine);
    }

    private static void Emit(MemoryStream line, Encoding fallback, Action<string> onLine)
    {
        var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);
        var text = Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : fallback.GetString(bytes);
        line.SetLength(0);
        onLine(text);
    }
}
