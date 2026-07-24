using System.Collections.Generic;
using System.Text;
using DockerDashboard.Models;

namespace DockerDashboard.Services;

public static class FastDevComposeGenerator
{
    public static string ProjectDirRelative(FastDevConfig config)
    {
        var slash = config.CsprojRelativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : config.CsprojRelativePath[..slash];
    }

    public static string ContainerDllPath(FastDevConfig config) =>
        $"/app/bin/Debug/{config.Tfm}/{config.AssemblyName}.dll";

    // 單一服務的 override 區塊（不含 services: 標頭），供整包合併
    public static string ServiceOverrideBlock(
        string serviceName, FastDevConfig config, string projectDirHostPath, string nugetHostPath)
    {
        var dll = ContainerDllPath(config);
        var sb = new StringBuilder();
        sb.AppendLine($"  {serviceName}:");
        // 只 build Dockerfile 的 base 階段（純 aspnet runtime，不含 code），對齊 VS 的 build.target: base：
        // 秒殺、不做完整 image build；build context/dockerfile 由 docker-compose.build.yml 提供
        sb.AppendLine("    build:");
        sb.AppendLine("      target: base");
        // ContentRoot 需為 /app 才讀得到 /app/appsettings.json（含反向代理前綴等設定），對齊 VS 的 workingdirectory
        sb.AppendLine("    working_dir: /app");
        sb.AppendLine("    volumes:");
        sb.AppendLine($"      - {YamlQuote($"{projectDirHostPath}:/app:rw")}");
        // 掛 /.nuget/packages（非 /root/...）：base 階段 USER $APP_UID 為非 root，讀不到 /root（700），對齊 VS
        sb.AppendLine($"      - {YamlQuote($"{nugetHostPath}:/.nuget/packages:ro")}");
        sb.AppendLine("    environment:");
        sb.AppendLine("      - ASPNETCORE_ENVIRONMENT=Development");
        // 強制聽 8080：nginx 反向代理寫死 proxy_pass ...:8080，容器沒聽 8080 就轉發失敗（nginx 404/502）
        sb.AppendLine("      - ASPNETCORE_URLS=http://+:8080");
        sb.AppendLine($"    entrypoint: [\"dotnet\", \"{dll}\", \"--additionalProbingPath\", \"/.nuget/packages\"]");
        return sb.ToString();
    }

    // 把多個服務區塊合成一份 override（整包一次 up 用）
    public static string CombineOverride(IEnumerable<string> serviceBlocks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("services:");
        foreach (var block in serviceBlocks)
            sb.Append(block);
        return sb.ToString();
    }

    // YAML 單引號：路徑含空白、# 或中文時 plain scalar 會被誤解析
    private static string YamlQuote(string value) =>
        $"'{value.Replace("'", "''")}'";
}
