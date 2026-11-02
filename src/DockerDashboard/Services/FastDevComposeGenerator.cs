using System.Collections.Generic;
using System.Text;
using System.Linq;
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
        string serviceName, FastDevConfig config, string projectDirHostPath, string nugetHostPath,
        string environment = ProjectEnvironment.Development, AzureCredential? credential = null)
    {
        var dll = ContainerDllPath(config);
        var sb = new StringBuilder();
        sb.AppendLine($"  {serviceName}:");
        // 用獨立 tag :fastdev，避免 build.target base 蓋掉正式 image（正式 image 是含 code 的完整版，蓋掉後一般 AllUp 會起不來），對齊 VS 的 :dev
        sb.AppendLine($"    image: {serviceName}:fastdev");
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
        sb.AppendLine($"      - ASPNETCORE_ENVIRONMENT={environment}");
        // 非 Development 時 .NET 服務改用 DefaultAzureCredential，由環境變數提供身分
        if (environment != ProjectEnvironment.Development && credential != null)
        {
            sb.AppendLine($"      - {EnvEntry("AZURE_TENANT_ID", credential.TenantId)}");
            sb.AppendLine($"      - {EnvEntry("AZURE_CLIENT_ID", credential.ClientId)}");
            sb.AppendLine($"      - {EnvEntry("AZURE_CLIENT_SECRET", credential.ClientSecret)}");
        }
        // 強制聽 8080：nginx 反向代理寫死 proxy_pass ...:8080，容器沒聽 8080 就轉發失敗（nginx 404/502）
        sb.AppendLine("      - ASPNETCORE_URLS=http://+:8080");
        // 指向不存在的 manifest：staticwebassets.runtime.json 內是 host Windows 路徑，容器載入會 crash（The path must be absolute）；
        // 設此值讓 .NET 找不到就跳過，app 照跑、wwwroot 仍由 UseStaticFiles 提供
        sb.AppendLine("      - ASPNETCORE_STATICWEBASSETS=/app/__nostaticwebassets__.json");
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

    // compose 會展開 $（變數插值），字面的 $ 必須跳脫成 $$
    internal static string EscapeComposeDollar(string value) => value.Replace("$", "$$");

    // 憑證值可能出現在 compose 輸出的所有寫法（原值、$ 跳脫、YAML 單引號加倍），供日誌遮蔽用
    internal static IEnumerable<string> SecretForms(string secret)
    {
        var escaped = EscapeComposeDollar(secret);
        return new[] { secret, escaped, secret.Replace("'", "''"), escaped.Replace("'", "''") }.Distinct();
    }

    private static string EnvEntry(string name, string value) =>
        YamlQuote($"{name}={EscapeComposeDollar(value)}");

    // YAML 單引號：路徑含空白、# 或中文時 plain scalar 會被誤解析
    private static string YamlQuote(string value) =>
        $"'{value.Replace("'", "''")}'";
}
