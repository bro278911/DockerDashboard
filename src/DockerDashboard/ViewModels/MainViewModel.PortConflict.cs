using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DockerDashboard.Models;

namespace DockerDashboard.ViewModels;

public partial class MainViewModel
{
    // Ports 為 compose 掃描時填入的 "published:target"（逗號分隔），沒 host bind 的項目不含冒號、不佔用 port
    private static IEnumerable<int> ParseComposeHostPorts(string? ports)
    {
        if (string.IsNullOrWhiteSpace(ports)) yield break;
        foreach (var token in ports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var colonIdx = token.IndexOf(':');
            if (colonIdx <= 0) continue;
            if (int.TryParse(token[..colonIdx], out var hostPort))
                yield return hostPort;
        }
    }

    // 檢查 scope 要綁的 host port 是否跟其他執行中服務衝突，衝突時詢問使用者要不要停止衝突服務後繼續
    private async Task<bool> EnsureNoPortConflictAsync(IReadOnlyList<DockerService> scope)
    {
        var scopeSet = new HashSet<DockerService>(scope);
        var desired = scope.SelectMany(s => ParseComposeHostPorts(s.Ports).Select(p => (Port: p, Wanted: s))).ToList();
        if (desired.Count == 0) return true;

        var conflicts = new List<(int Port, DockerService Wanted, DockerService Running)>();
        foreach (var other in AllServices())
        {
            if (!other.IsRunning || scopeSet.Contains(other)) continue;
            var otherPorts = ParseComposeHostPorts(other.Ports).ToHashSet();
            foreach (var (port, wanted) in desired)
                if (otherPorts.Contains(port))
                    conflicts.Add((port, wanted, other));
        }
        if (conflicts.Count == 0) return true;

        return await ConfirmAndStopPortConflictsAsync(conflicts);
    }

    private async Task<bool> ConfirmAndStopPortConflictsAsync(List<(int Port, DockerService Wanted, DockerService Running)> conflicts)
    {
        var lines = conflicts
            .Select(c => $"Port {c.Port}：{OwnerLabel(c.Running)} 佔用（{c.Wanted.Name} 需要）")
            .Distinct();
        var message = $"以下 Port 已被其他執行中服務佔用：\n\n{string.Join("\n", lines)}\n\n是否停止上述服務並繼續啟動？";
        var result = System.Windows.MessageBox.Show(message, "Port 衝突", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return false;

        foreach (var svc in conflicts.Select(c => c.Running).Distinct())
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ 停止衝突服務 {svc.Name}（Port 衝突釋放）");
            await _dockerCli.ComposeStopWithLogAsync(svc.WorkingDirectory, AppendLog, svc.Name, CancellationToken.None);
        }
        await _monitor.ForceRefreshAsync();
        return true;
    }

    private string OwnerLabel(DockerService service)
    {
        var project = Projects.FirstOrDefault(p => p.ComposeFiles.Any(c => c.Services.Contains(service)));
        return project != null ? $"{project.Name}/{service.Name}" : service.Name;
    }
}
