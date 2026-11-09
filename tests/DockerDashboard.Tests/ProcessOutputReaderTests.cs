using System.IO;
using System.Text;
using DockerDashboard.Services;

namespace DockerDashboard.Tests;

public class ProcessOutputReaderTests
{
    private static readonly Encoding Oem = ProcessOutputReader.OemEncoding;

    private static async Task<List<string>> ReadAsync(byte[] bytes)
    {
        var lines = new List<string>();
        await ProcessOutputReader.ReadLinesAsync(new MemoryStream(bytes), lines.Add, CancellationToken.None);
        return lines;
    }

    // cmd.exe 自身訊息（找不到指令）走 OEM codepage，npm/vite 輸出走 UTF-8，兩者會混在同一條 stderr。
    [Fact]
    public async Task ReadLinesAsync_同一串流混合UTF8與OEM行_各自正確解碼()
    {
        const string utf8Line = "VITE v5 ready ✓ 中文";
        const string oemLine = "'vite' 不是內部或外部命令、可執行的程式或批次檔。";
        var bytes = Encoding.UTF8.GetBytes(utf8Line + "\r\n")
            .Concat(Oem.GetBytes(oemLine + "\r\n"))
            .ToArray();

        var lines = await ReadAsync(bytes);

        Assert.Equal([utf8Line, oemLine], lines);
    }

    // 多位元組字元剛好被 4096 bytes 的讀取緩衝切開時，不能解成 U+FFFD
    [Fact]
    public async Task ReadLinesAsync_多位元組字元跨讀取緩衝邊界_不產生替代字元()
    {
        var line = new string('a', 4095) + "中" + new string('b', 10);

        var lines = await ReadAsync(Encoding.UTF8.GetBytes(line + "\n"));

        Assert.Equal([line], lines);
    }

    // 與 StreamReader.ReadLine 相同：\r\n 一行、單獨 \r 或 \n 也各算一行，且 \r\n 跨緩衝邊界不得多出空行
    [Fact]
    public async Task ReadLinesAsync_換行符號_與ReadLine語意一致()
    {
        var text = "a\r\nb\rc\nd" + new string('x', 4094) + "\r\ne";

        var lines = await ReadAsync(Encoding.UTF8.GetBytes(text));

        Assert.Equal(["a", "b", "c", "d" + new string('x', 4094), "e"], lines);
    }

    // \r 落在緩衝最後一個 byte、\n 在下一個緩衝開頭時仍屬同一個 \r\n
    [Fact]
    public async Task ReadLinesAsync_CR在緩衝尾端LF在下個緩衝開頭_不多出空行()
    {
        var first = "a" + new string('x', 4094);

        var lines = await ReadAsync(Encoding.UTF8.GetBytes(first + "\r\nb\n"));

        Assert.Equal([first, "b"], lines);
    }

    [Fact]
    public async Task ReadLinesAsync_結尾無換行_最後一行仍輸出()
    {
        var lines = await ReadAsync(Encoding.UTF8.GetBytes("a\nb"));

        Assert.Equal(["a", "b"], lines);
    }

    // 切換日誌時取消舊串流，已讀進緩衝但尚未輸出的行不得再灌進新日誌
    [Fact]
    public async Task ReadLinesAsync_讀取後被取消_不再輸出緩衝內的行()
    {
        using var cts = new CancellationTokenSource();
        var stream = new CancelAfterReadStream(Encoding.UTF8.GetBytes("a\nb\nc"), cts);
        var lines = new List<string>();

        await ProcessOutputReader.ReadLinesAsync(stream, lines.Add, cts.Token);

        Assert.Empty(lines);
    }

    private sealed class CancelAfterReadStream(byte[] bytes, CancellationTokenSource cts) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await base.ReadAsync(buffer, ct);
            cts.Cancel();
            return read;
        }
    }

    [Fact]
    public async Task ReadAllAsync_cmd找不到指令_錯誤訊息不含亂碼()
    {
        var service = new NodeProcessService();
        var lines = new List<string>();

        using var stream = service.Start(Path.GetTempPath(), "notexistcmd123");
        await ProcessOutputReader.ReadAllAsync(stream, lines.Add, CancellationToken.None);
        await stream.WaitForExitAsync();

        Assert.NotEqual(0, stream.ExitCode);
        var message = Assert.Single(lines, l => l.Contains("notexistcmd123"));
        Assert.DoesNotContain('�', message);
    }
}
