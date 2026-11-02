using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DockerDashboard.Services;

// 專案層級的 ASPNETCORE_ENVIRONMENT 選擇：名稱常數、設定讀寫、可選環境偵測
public static class ProjectEnvironment
{
    public const string Development = "Development";
    public const string Stage = "Stage";
    public const string Production = "Production";

    public static readonly IReadOnlyList<string> All = [Development, Stage, Production];

    // JSON 反序列化出來的 Dictionary 不保留 OrdinalIgnoreCase，故自行比對 key；
    // 值不在已知三者內（手改設定檔）一律視為 Development
    public static string GetStored(IReadOnlyDictionary<string, string>? map, string folderPath)
    {
        if (map == null) return Development;
        foreach (var (key, value) in map)
        {
            if (!key.Equals(folderPath, StringComparison.OrdinalIgnoreCase)) continue;
            return All.FirstOrDefault(e => e.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? Development;
        }
        return Development;
    }

    // Development 是預設值，不存進設定檔
    public static void Set(Dictionary<string, string> map, string folderPath, string environment)
    {
        foreach (var key in map.Keys.Where(k => k.Equals(folderPath, StringComparison.OrdinalIgnoreCase)).ToList())
            map.Remove(key);
        if (environment != Development)
            map[folderPath] = environment;
    }

    // Development 永遠可選（預設值，不需要 appsettings.Development.json 也能跑）；
    // Stage／Production 只在任一 csproj 目錄真的有 appsettings.<Env>.json 時才列入，Production 另需 allowProduction
    public static List<string> DetectAvailable(IEnumerable<string> workingDirectories, bool allowProduction)
    {
        var wanted = allowProduction ? new[] { Stage, Production } : new[] { Stage };
        var found = new HashSet<string>();
        foreach (var workingDirectory in workingDirectories)
        {
            foreach (var csproj in FastDevDetector.EnumerateCsprojRelative(workingDirectory))
            {
                var projectDir = Path.GetDirectoryName(
                    Path.Combine(workingDirectory, csproj.Replace('/', Path.DirectorySeparatorChar)));
                if (projectDir == null) continue;
                foreach (var env in wanted.Where(e => !found.Contains(e)))
                {
                    if (File.Exists(Path.Combine(projectDir, $"appsettings.{env}.json")))
                        found.Add(env);
                }
                if (found.Count == wanted.Length) break;
            }
        }
        return [Development, .. All.Where(found.Contains)];
    }
}
