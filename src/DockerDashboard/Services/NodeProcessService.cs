using System.Diagnostics;
using System.Text;

namespace DockerDashboard.Services;

/// <summary>啟動前端 npm 類指令（cmd /c 包裝）。不維護狀態，行程生命週期由呼叫端管理。</summary>
public class NodeProcessService
{
    public ProcessStream Start(string folderPath, string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = folderPath,
        };
        // ArgumentList 會用 CRT 規則跳脫，把使用者指令裡的引號改成 cmd 不認得的 \"（例如 E2E
        // 常見的 --grep "xxx"）；改用 /s /c 傳原始命令列讓 cmd 自行處理引號。
        // cmd.exe 自身的錯誤訊息走系統 OEM codepage 而非 UTF-8，由 ProcessOutputReader 逐行判斷編碼。
        psi.Arguments = $"/s /c \"{command}\"";

        var process = ProcessLauncher.Start(psi);
        return new ProcessStream(process);
    }
}
