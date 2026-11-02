using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DockerDashboard.Services;

// 刻意不用 record：record 的 ToString 會印出所有成員（含 secret）
public sealed class AzureCredential(string tenantId, string clientId, string clientSecret)
{
    public string TenantId { get; } = tenantId;
    public string ClientId { get; } = clientId;
    public string ClientSecret { get; } = clientSecret;

    public override string ToString() => nameof(AzureCredential);
}

// 非 Development 環境下 .NET 服務會用 DefaultAzureCredential 取得身分；
// 這裡從 appsettings.<Env>.json 的 AppRegistration 找出可注入容器的 AZURE_* 環境變數值
public static class EnvironmentCredentialResolver
{
    public const string NotFoundMessage = "找不到 Azure 憑證，服務可能無法取得身分";

    private const string PreferredFallbackName = "AuthenticationBackend";

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // 同 WorkingDirectory 下所有 csproj 目錄，AuthenticationBackend 優先
    public static List<string> EnumerateFallbackDirs(string workingDirectory) =>
        FastDevDetector.EnumerateCsprojRelative(workingDirectory)
            .Select(csproj => Path.GetDirectoryName(
                Path.Combine(workingDirectory, csproj.Replace('/', Path.DirectorySeparatorChar))))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(dir => Path.GetFileName(dir).Contains(PreferredFallbackName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

    // log 只會收到固定訊息，不含任何憑證值
    public static AzureCredential? Resolve(
        string serviceProjectDir, IEnumerable<string> fallbackDirs, string environment, Action<string>? log = null)
    {
        foreach (var dir in new[] { serviceProjectDir }.Concat(fallbackDirs).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var credential = TryRead(Path.Combine(dir, $"appsettings.{environment}.json"));
            if (credential != null) return credential;
        }
        log?.Invoke(NotFoundMessage);
        return null;
    }

    private static AzureCredential? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            // ReadAllText 會吃掉 UTF-8 BOM
            using var doc = JsonDocument.Parse(File.ReadAllText(path), ParseOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (Child(doc.RootElement, "AppRegistration") is not { ValueKind: JsonValueKind.Object } section) return null;

            var tenant = Text(section, "TenantId");
            var client = Text(section, "ClientId");
            var secret = Text(section, "ClientSecret");
            return tenant == null || client == null || secret == null
                ? null
                : new AzureCredential(tenant, client, secret);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // .NET 設定鍵不分大小寫
    private static JsonElement? Child(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }
        return null;
    }

    // 空白或含換行／控制字元的值視為不可用：換行會破壞寫進 override 的 YAML
    private static string? Text(JsonElement element, string name)
    {
        if (Child(element, name) is not { ValueKind: JsonValueKind.String } value) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl) ? null : text;
    }
}
