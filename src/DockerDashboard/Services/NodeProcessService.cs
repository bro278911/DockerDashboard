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
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(command);

        var process = new Process { StartInfo = psi };
        process.Start();
        return new ProcessStream(process);
    }
}
