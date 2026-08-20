using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DockerDashboard.Models;
using DockerDashboard.Services;

namespace DockerDashboard.ViewModels;

public partial class MainViewModel
{
    // Ports 有兩種來源格式（逗號分隔）：
    // 1. compose 掃描填入的 "published:target"（可帶 host IP 前綴），沒 host bind 的項目不含冒號、不佔用 port
    // 2. YAML fallback 掃描未填 Ports 時，OnContainersUpdated 以 docker ps 格式回填（如 "0.0.0.0:3001->80/tcp"）
    private static IEnumerable<int> ParseComposeHostPorts(string? ports)
    {
        if (string.IsNullOrWhiteSpace(ports)) yield break;
        foreach (var token in ports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // docker ps 格式："IP:hostPort->containerPort/proto"，host port 在 -> 前、最後一個冒號後
            var arrowIdx = token.IndexOf("->", StringComparison.Ordinal);
            if (arrowIdx > 0)
            {
                var head = token[..arrowIdx];
                var idx = head.LastIndexOf(':');
                if (idx >= 0 && int.TryParse(head[(idx + 1)..], out var mappedPort))
                    yield return mappedPort;
                continue;
            }

            // compose 格式："hostPort:target" 或 "ip:hostPort:target"（target 可帶 /proto），host port 在倒數第二段
            var parts = token.Split(':');
            if (parts.Length >= 2 && int.TryParse(parts[^2], out var hostPort))
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

        // 停止衝突服務的 await 期間 UI 未被鎖住，須佔用 _operationCts 防止使用者疊加第二個操作；
        // 結束後釋放，讓呼叫端後續的 TryBeginOperation 能接手（同為 UI 執行緒接續，中間不會被插隊）
        if (!TryBeginOperation()) return false;
        var cts = _operationCts!;
        IsOperating = true;
        try
        {
            var stopped = conflicts.Select(c => c.Running).Distinct().ToList();
            foreach (var svc in stopped)
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ⏹ 停止衝突服務 {svc.Name}（Port 衝突釋放）");
                var (exitCode, _) = await _dockerCli.ComposeStopWithLogAsync(svc.WorkingDirectory, AppendLog, svc.Name, CancellationToken.None);
                if (exitCode != 0)
                {
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ 停止 {svc.Name} 失敗 (exit {exitCode})，Port 可能仍被佔用，已中止啟動");
                    return false;
                }
            }
            await ClearFastDevStateAsync(stopped.Where(s => s.IsFastDev).ToList());
            await _monitor.ForceRefreshAsync();
            return true;
        }
        finally
        {
            if (ReferenceEquals(_operationCts, cts))
                _operationCts = null;
            cts.Dispose();
            IsOperating = false;
        }
    }

    // 被停掉的衝突服務若在 Fast Dev 中，同步清掉旗標與持久化狀態，避免 settings 與實際容器狀態脫鉤
    private async Task ClearFastDevStateAsync(IReadOnlyList<DockerService> stoppedFastDev)
    {
        if (stoppedFastDev.Count == 0) return;

        var settings = await _settingsService.LoadAsync();
        foreach (var svc in stoppedFastDev)
        {
            svc.IsFastDev = false;
            PersistFastDev(settings, svc.WatchKey, null, enabled: false);
        }
        await _settingsService.SaveAsync(settings);

        // 同目錄已無 Fast Dev 服務才清共用資源（override 檔與 watcher），與啟用失敗的回滾邏輯一致
        foreach (var dir in stoppedFastDev.Select(s => s.WorkingDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hasRemaining = AllServices().Any(s => s.IsFastDev &&
                string.Equals(s.WorkingDirectory, dir, StringComparison.OrdinalIgnoreCase));
            if (!hasRemaining)
            {
                FastDevOverrideStore.Delete(dir);
                _fastDevReload.Unwatch(dir);
            }
        }
    }

    private string OwnerLabel(DockerService service)
    {
        var project = Projects.FirstOrDefault(p => p.ComposeFiles.Any(c => c.Services.Contains(service)));
        return project != null ? $"{project.Name}/{service.Name}" : service.Name;
    }
}
