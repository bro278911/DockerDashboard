using System.IO;
using System.Text;
using DockerDashboard.Services;

namespace DockerDashboard.Tests;

// 所有憑證皆為測試用假值
public class EnvironmentCredentialResolverTests : IDisposable
{
    private const string FakeSecret = "fake-secret-for-test-only";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cred-" + Guid.NewGuid().ToString("N"));

    public EnvironmentCredentialResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private string ServiceDir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".csproj"), "<Project />");
        return dir;
    }

    private static void WriteSettings(string dir, string env, string json, bool bom = false) =>
        File.WriteAllText(Path.Combine(dir, $"appsettings.{env}.json"), json, new UTF8Encoding(bom));

    private static string Registration(string tenant = "t-1", string client = "c-1", string secret = FakeSecret) =>
        $$"""{ "AppRegistration": { "TenantId": "{{tenant}}", "ClientId": "{{client}}", "ClientSecret": "{{secret}}" } }""";

    [Fact]
    public void 服務自己的設定檔有AppRegistration_直接使用()
    {
        var dir = ServiceDir("OrderBackend");
        WriteSettings(dir, "Stage", Registration());

        var cred = EnvironmentCredentialResolver.Resolve(dir, [], "Stage");

        Assert.NotNull(cred);
        Assert.Equal("t-1", cred.TenantId);
        Assert.Equal("c-1", cred.ClientId);
        Assert.Equal(FakeSecret, cred.ClientSecret);
    }

    [Fact]
    public void 服務缺欄位_退到AuthenticationBackend且優先於其他服務()
    {
        var order = ServiceDir("OrderBackend");
        WriteSettings(order, "Stage", """{ "AppRegistration": { "TenantId": "only-tenant" } }""");
        var other = ServiceDir("AaaBackend");
        WriteSettings(other, "Stage", Registration(client: "from-other"));
        var auth = ServiceDir("AuthenticationBackend");
        WriteSettings(auth, "Stage", Registration(client: "from-auth"));

        var fallback = EnvironmentCredentialResolver.EnumerateFallbackDirs(_root);
        var cred = EnvironmentCredentialResolver.Resolve(order, fallback, "Stage");

        Assert.Equal("from-auth", cred?.ClientId);
    }

    [Fact]
    public void 沒有任何憑證_回傳null且只記固定訊息()
    {
        var order = ServiceDir("OrderBackend");
        WriteSettings(order, "Stage", """{ "Logging": { "Level": "Info" } }""");
        var logs = new List<string>();

        var cred = EnvironmentCredentialResolver.Resolve(
            order, EnvironmentCredentialResolver.EnumerateFallbackDirs(_root), "Stage", logs.Add);

        Assert.Null(cred);
        Assert.Equal([EnvironmentCredentialResolver.NotFoundMessage], logs);
    }

    [Fact]
    public void 環境檔不同_不會讀到別的環境的憑證()
    {
        var auth = ServiceDir("AuthenticationBackend");
        WriteSettings(auth, "Production", Registration());

        Assert.Null(EnvironmentCredentialResolver.Resolve(auth, [], "Stage"));
    }

    [Fact]
    public void 允許註解_BOM_尾逗號_鍵不分大小寫()
    {
        var dir = ServiceDir("AuthenticationBackend");
        WriteSettings(dir, "Stage", """
            {
              // 註解
              "appregistration": { "tenantid": "t", "CLIENTID": "c", "ClientSecret": "s", },
            }
            """, bom: true);

        Assert.Equal("s", EnvironmentCredentialResolver.Resolve(dir, [], "Stage")?.ClientSecret);
    }

    [Fact]
    public void JSON損毀_回傳null不拋例外()
    {
        var dir = ServiceDir("AuthenticationBackend");
        WriteSettings(dir, "Stage", "{ not json");

        Assert.Null(EnvironmentCredentialResolver.Resolve(dir, [], "Stage"));
    }

    [Fact]
    public void 值為空白或含換行_視為不可用()
    {
        var dir = ServiceDir("AuthenticationBackend");
        WriteSettings(dir, "Stage", """{ "AppRegistration": { "TenantId": "t", "ClientId": "c", "ClientSecret": "a\nb" } }""");

        Assert.Null(EnvironmentCredentialResolver.Resolve(dir, [], "Stage"));
    }

    [Fact]
    public void 憑證物件ToString不含任何值()
    {
        var cred = new AzureCredential("tenant-x", "client-x", FakeSecret);

        Assert.DoesNotContain(FakeSecret, cred.ToString());
        Assert.DoesNotContain("tenant-x", cred.ToString());
        Assert.DoesNotContain("client-x", $"{cred}");
    }
}
