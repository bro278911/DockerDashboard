using System.Diagnostics;

namespace DockerDashboard.Services;

public readonly record struct PortListener(int Pid, string Address);

public static class PortListenerScanner
{
    public static IReadOnlyList<PortListener> Parse(string netstatOutput, int port)
    {
        var listeners = new List<PortListener>();
        var seenPids = new HashSet<int>();
        var portSuffix = $":{port}";

        foreach (var line in netstatOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 5 ||
                !string.Equals(tokens[0], "TCP", StringComparison.Ordinal) ||
                !string.Equals(tokens[3], "LISTENING", StringComparison.Ordinal) ||
                !tokens[1].EndsWith(portSuffix, StringComparison.Ordinal) ||
                !int.TryParse(tokens[4], out var pid) ||
                pid <= 4 ||
                !seenPids.Add(pid))
            {
                continue;
            }

            listeners.Add(new PortListener(pid, tokens[1]));
        }

        return listeners;
    }

    public static IReadOnlyList<PortListener> Scan(int port)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "netstat",
            Arguments = "-ano -p TCP",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("無法啟動 netstat。");

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return Parse(output, port);
    }
}
