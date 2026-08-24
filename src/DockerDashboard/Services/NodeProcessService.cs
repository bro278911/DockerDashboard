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
        // 已知限制：cmd.exe 自身的錯誤訊息（非 npm/node 產生，例如指令打錯的「不是內部或外部
        // 命令」）在 redirect 情境下一律走系統 OEM codepage，不受 chcp 影響、也無法用單一
        // StandardErrorEncoding 設定同時兼顧 npm/vite 自己的 UTF-8 輸出，故不處理；exit code
        // 與其餘輸出仍正確，只是這類訊息本身顯示為亂碼。
        psi.Arguments = $"/s /c \"{command}\"";

        var process = new Process { StartInfo = psi };
        process.Start();
        return new ProcessStream(process);
    }
}
