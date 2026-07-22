using System;
using System.Collections.Generic;
using DockerDashboard.Models;

namespace DockerDashboard.Services;

// 容器與 compose service 比對：ContainerName → 資料夾 → compose project → 寬鬆（同名跨資料夾時禁用）
public sealed class ContainerMatcher
{
    private const string SvcPrefix = "com.docker.compose.service=";
    private const string ProjPrefix = "com.docker.compose.project=";
    private const string DirPrefix = "com.docker.compose.project.working_dir=";

    private readonly Dictionary<string, ContainerInfo> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ContainerInfo> _byComposeService = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ContainerInfo> _byDirService = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ContainerInfo> _byProjectService = new(StringComparer.OrdinalIgnoreCase);

    public ContainerMatcher(IEnumerable<ContainerInfo> containers)
    {
        foreach (var c in containers)
        {
            foreach (var name in c.Names.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
                AddPreferRunning(_byName, name.TrimStart('/'), c);

            string? svc = null;
            string? proj = null;
            foreach (var label in c.Labels.Split(','))
            {
                if (label.StartsWith(SvcPrefix, StringComparison.OrdinalIgnoreCase))
                    svc = label[SvcPrefix.Length..].Trim();
                else if (label.StartsWith(ProjPrefix, StringComparison.OrdinalIgnoreCase))
                    proj = label[ProjPrefix.Length..].Trim();
            }

            if (svc == null) continue;

            // working_dir 值可能含逗號，不能用逗號切割；以下一個已知 label 前綴當終點
            string? workDir = null;
            var dirIdx = c.Labels.IndexOf(DirPrefix, StringComparison.OrdinalIgnoreCase);
            if (dirIdx >= 0)
            {
                var start = dirIdx + DirPrefix.Length;
                var end = c.Labels.IndexOf(",com.docker.", start, StringComparison.OrdinalIgnoreCase);
                if (end < 0) end = c.Labels.IndexOf(",desktop.", start, StringComparison.OrdinalIgnoreCase);
                workDir = (end < 0 ? c.Labels[start..] : c.Labels[start..end]).Trim();
            }

            if (!string.IsNullOrEmpty(proj))
                AddPreferRunning(_byProjectService, $"{proj}|{svc}", c);

            // 有資料夾 label 的容器只走資料夾/project 比對，避免誤配到未匯入的同名 service
            if (workDir != null)
                AddPreferRunning(_byDirService, $"{NormalizePath(workDir)}|{svc}", c);
            else
                AddPreferRunning(_byComposeService, svc, c);
        }
    }

    public ContainerInfo? Resolve(
        string serviceName, string? containerName, string workingDirectory,
        string projectName, ISet<string> ambiguousNames)
    {
        ContainerInfo? match = null;

        if (!string.IsNullOrEmpty(containerName))
            _byName.TryGetValue(containerName, out match);

        match ??= _byDirService.GetValueOrDefault($"{NormalizePath(workingDirectory)}|{serviceName}");
        match ??= _byDirService.GetValueOrDefault(
            $"{NormalizePath(DockerCliService.ConvertToWslPath(workingDirectory))}|{serviceName}");

        // 同一 compose project（相同 name）從不同資料夾 up 會互相 replace 容器，
        // working_dir label 因此指向最後執行 up 的資料夾；以 project name 對應才反映實際狀態
        if (!string.IsNullOrEmpty(projectName))
            match ??= _byProjectService.GetValueOrDefault($"{projectName}|{serviceName}");

        if (!ambiguousNames.Contains(serviceName))
        {
            match ??= _byComposeService.GetValueOrDefault(serviceName);
            match ??= _byName.GetValueOrDefault(serviceName);
        }

        return match;
    }

    // 同 key 撞名時偏好 running 容器（如舊 exited 容器殘留），其餘維持先進先贏
    private static void AddPreferRunning(Dictionary<string, ContainerInfo> dict, string key, ContainerInfo c)
    {
        if (dict.TryGetValue(key, out var existing) && (IsRunning(existing) || !IsRunning(c)))
            return;
        dict[key] = c;
    }

    private static bool IsRunning(ContainerInfo c) =>
        string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimEnd('/');
}
