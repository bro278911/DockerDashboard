using System.IO;
using DockerDashboard.Models;
using DockerDashboard.Services;
using DockerDashboard.ViewModels;

namespace DockerDashboard.Tests;

// 統一服務啟動流程：單一核心 StartServicesAsync 取代舊的整包 up / 無 override 的 up
public class MainViewModelStartupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "startup-" + Guid.NewGuid().ToString("N"));
    private readonly string _dir;
    private readonly FakeCli _cli = new();
    private readonly FakeHostBuild _build = new();
    private readonly SettingsService _settings;
    private readonly MainViewModel _vm;
    private readonly DockerService _api;
    private readonly DockerService _orders;
    private readonly DockerService _nginx;

    public MainViewModelStartupTests()
    {
        _dir = Path.Combine(_root, "proj");
        foreach (var name in new[] { "api", "orders" })
        {
            Directory.CreateDirectory(Path.Combine(_dir, name));
            File.WriteAllText(Path.Combine(_dir, name, name + ".csproj"),
                "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        }
        File.WriteAllText(Path.Combine(_dir, "docker-compose.yml"), "services: {}");

        _settings = new SettingsService(Path.Combine(_root, "settings.json"));
        _vm = new MainViewModel(
            _cli, new FakeGit(), new ComposeFileScanner(new ScanCacheService(Path.Combine(_root, "cache.json"))),
            _settings, new ContainerMonitorService(_cli), _build, new FastDevReloadService(),
            // UpdateService 建構子需要 Velopack 安裝環境，測試用不到更新功能，跳過建構子
            (UpdateService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(UpdateService)),
            new FrontendProcessManager(new NodeProcessService()))
        {
            _dotnetSdkChecked = true,
            _dotnetSdkAvailable = true,
        };

        _api = NewService("api");
        _orders = NewService("orders");
        _nginx = NewService("nginx");
        var compose = new ComposeFile { DirectoryPath = _dir };
        compose.Services.Add(_api);
        compose.Services.Add(_orders);
        compose.Services.Add(_nginx);
        var project = new DockerProject { Name = "proj", FolderPath = _dir };
        project.ComposeFiles.Add(compose);
        _vm.Projects.Add(project);
    }

    public void Dispose()
    {
        _vm.Dispose();
        FastDevOverrideStore.Delete(_dir);
        Directory.Delete(_root, true);
    }

    private DockerService NewService(string name) => new()
    {
        Name = name,
        WorkingDirectory = _dir,
        ComposeFilePath = Path.Combine(_dir, "docker-compose.yml"),
    };

    // 模擬「orders 先前已在 Fast Dev」：旗標、持久化設定都就位
    private async Task MakeFastDevAsync(DockerService service, string container)
    {
        var settings = await _settings.LoadAsync();
        MainViewModel.PersistFastDev(settings, service.WatchKey, new FastDevConfig
        {
            ServiceKey = service.WatchKey,
            CsprojRelativePath = $"{service.Name}/{service.Name}.csproj",
            RuntimeImage = "aspnet",
            Tfm = "net10.0",
            AssemblyName = service.Name,
            SrcRoot = _dir,
        }, enabled: true);
        await _settings.SaveAsync(settings);
        service.IsFastDev = true;
        service.Status = ContainerStatus.Running;
        service.ContainerId = container;
    }

    [Fact]
    public async Task 單服務啟動_只把該服務名傳給up且只build該專案()
    {
        await _vm.StartServicesAsync(_dir, [_api]);

        var up = Assert.Single(_cli.UpCalls);
        Assert.Equal(["api"], up.Names);
        Assert.NotNull(up.OverridePath);
        var built = Assert.Single(_build.Calls);
        Assert.Equal([Path.Combine(_dir, "api", "api.csproj")], built);
        Assert.True(_api.IsFastDev);
    }

    [Fact]
    public async Task override包含scope外既有的FastDev服務()
    {
        await MakeFastDevAsync(_orders, "orders-cid");

        await _vm.StartServicesAsync(_dir, [_api]);

        var up = Assert.Single(_cli.UpCalls);
        Assert.Equal(["api"], up.Names);
        Assert.Contains("  api:", up.OverrideText);
        Assert.Contains("  orders:", up.OverrideText);
        // scope 外的 orders 不重新 build
        Assert.Equal([Path.Combine(_dir, "api", "api.csproj")], Assert.Single(_build.Calls));
    }

    [Fact]
    public async Task 非dotnet服務_不需SDK仍照常up且不build()
    {
        _vm._dotnetSdkAvailable = false;

        var ok = await _vm.StartServicesAsync(_dir, [_nginx]);

        Assert.True(ok);
        var up = Assert.Single(_cli.UpCalls);
        Assert.Equal(["nginx"], up.Names);
        Assert.Null(up.OverridePath);
        Assert.Empty(_build.Calls);
        Assert.False(_nginx.IsFastDev);
    }

    [Fact]
    public async Task 有dotnet服務但無SDK_不啟動且不留旗標()
    {
        _vm._dotnetSdkAvailable = false;

        var ok = await _vm.StartServicesAsync(_dir, [_api]);

        Assert.False(ok);
        Assert.Empty(_cli.UpCalls);
        Assert.False(_api.IsFastDev);
    }

    [Fact]
    public async Task up失敗_回滾新啟用的旗標與持久化_保留既有FastDev的override()
    {
        await MakeFastDevAsync(_orders, "orders-cid");
        _cli.UpExitCode = 1;

        var ok = await _vm.StartServicesAsync(_dir, [_api]);

        Assert.False(ok);
        Assert.False(_api.IsFastDev);
        Assert.True(_orders.IsFastDev);
        var settings = await _settings.LoadAsync();
        Assert.DoesNotContain(_api.WatchKey, settings.FastDevEnabledServiceKeys);
        Assert.Contains(_orders.WatchKey, settings.FastDevEnabledServiceKeys);
        var overrideText = File.ReadAllText(FastDevOverrideStore.PathFor(_dir));
        Assert.Contains("  orders:", overrideText);
        Assert.DoesNotContain("  api:", overrideText);
        Assert.Single(_cli.UpCalls);
    }

    [Fact]
    public async Task up失敗且無其他FastDev_刪除override()
    {
        _cli.UpExitCode = 1;

        await _vm.StartServicesAsync(_dir, [_api]);

        Assert.False(_api.IsFastDev);
        Assert.False(File.Exists(FastDevOverrideStore.PathFor(_dir)));
    }

    [Fact]
    public async Task build失敗_不up並回滾()
    {
        _build.ExitCode = 1;

        var ok = await _vm.StartServicesAsync(_dir, [_api]);

        Assert.False(ok);
        Assert.Empty(_cli.UpCalls);
        Assert.False(_api.IsFastDev);
    }

    [Fact]
    public async Task 完整重建_只動單一服務_其餘FastDev服務留在override()
    {
        await MakeFastDevAsync(_api, "api-cid");
        await MakeFastDevAsync(_orders, "orders-cid");
        _vm.ConfirmLeaveFastDev = _ => true;

        await _vm.RebuildRestartServiceCommand.ExecuteAsync(_api);

        var up = Assert.Single(_cli.UpCalls);
        Assert.Equal(["api"], up.Names);
        Assert.Contains("  orders:", up.OverrideText);
        Assert.DoesNotContain("  api:", up.OverrideText);
        Assert.False(_api.IsFastDev);
        Assert.True(_orders.IsFastDev);
        Assert.Empty(_build.Calls);
    }

    [Fact]
    public async Task 完整重建_使用者拒絕離開FastDev時不做任何事()
    {
        await MakeFastDevAsync(_api, "api-cid");
        _vm.ConfirmLeaveFastDev = _ => false;

        await _vm.RebuildRestartServiceCommand.ExecuteAsync(_api);

        Assert.Empty(_cli.UpCalls);
        Assert.True(_api.IsFastDev);
    }

    [Fact]
    public async Task 重啟_容器在跑時走RestartContainer_不重建()
    {
        await MakeFastDevAsync(_api, "api-cid");

        await _vm.RestartServiceCommand.ExecuteAsync(_api);

        Assert.Equal(["api-cid"], _cli.RestartCalls);
        Assert.Empty(_cli.UpCalls);
    }

    [Fact]
    public async Task 重啟_非FastDev但在跑也走RestartContainer()
    {
        _nginx.Status = ContainerStatus.Running;
        _nginx.ContainerId = "nginx-cid";

        await _vm.RestartServiceCommand.ExecuteAsync(_nginx);

        Assert.Equal(["nginx-cid"], _cli.RestartCalls);
        Assert.Empty(_cli.UpCalls);
    }

    [Fact]
    public async Task 重啟_exitCode非零時狀態列顯示失敗()
    {
        _nginx.Status = ContainerStatus.Running;
        _nginx.ContainerId = "nginx-cid";
        _cli.RestartExitCode = 1;

        await _vm.RestartServiceCommand.ExecuteAsync(_nginx);

        Assert.Contains("失敗", _vm.StatusMessage);
    }

    [Fact]
    public async Task 重啟_沒在跑時走統一啟動()
    {
        await _vm.RestartServiceCommand.ExecuteAsync(_nginx);

        Assert.Empty(_cli.RestartCalls);
        Assert.Equal(["nginx"], Assert.Single(_cli.UpCalls).Names);
    }

    [Fact]
    public async Task 套用最新code_dll沒變也一定重啟該服務_不重啟其他服務()
    {
        await MakeFastDevAsync(_api, "api-cid");
        await MakeFastDevAsync(_orders, "orders-cid");

        await _vm.ApplyLatestCodeServiceCommand.ExecuteAsync(_api);

        Assert.Equal(["api-cid"], _cli.RestartCalls);
        // 目錄內所有 Fast Dev 服務都做增量 build
        Assert.Equal(2, Assert.Single(_build.Calls, c => c.Count == 2).Count);
        Assert.Empty(_cli.UpCalls);
    }

    [Fact]
    public async Task 熱重載_沒指定forceRestart且dll沒變_不重啟()
    {
        await MakeFastDevAsync(_api, "api-cid");

        await _vm.OnFastDevSolutionChangedAsync(_dir);

        Assert.Empty(_cli.RestartCalls);
    }

    [Fact]
    public async Task 熱重載_重啟失敗時狀態列明示()
    {
        await MakeFastDevAsync(_api, "api-cid");
        _cli.RestartExitCode = 1;

        await _vm.OnFastDevSolutionChangedAsync(_dir, _api);

        Assert.Contains("api", _vm.StatusMessage);
        Assert.Contains("失敗", _vm.StatusMessage);
    }

    [Fact]
    public async Task 熱重載_已有操作進行中時略過不插隊()
    {
        await MakeFastDevAsync(_api, "api-cid");
        _vm.IsOperating = true;

        await _vm.OnFastDevSolutionChangedAsync(_dir, _api);

        Assert.Empty(_build.Calls);
        Assert.Empty(_cli.RestartCalls);
    }

    [Fact]
    public async Task 套用最新code_服務沒在FastDev時走統一啟動()
    {
        await _vm.ApplyLatestCodeServiceCommand.ExecuteAsync(_nginx);

        Assert.Equal(["nginx"], Assert.Single(_cli.UpCalls).Names);
        Assert.Empty(_cli.RestartCalls);
    }

    [Fact]
    public async Task 專案啟動_不踩掉FastDev_scope內全部服務一起up()
    {
        await MakeFastDevAsync(_orders, "orders-cid");

        await _vm.ProjectUpCommand.ExecuteAsync(_vm.Projects[0]);

        var up = Assert.Single(_cli.UpCalls);
        Assert.Equal(["api", "orders", "nginx"], up.Names);
        Assert.Contains("  orders:", up.OverrideText);
        Assert.True(_orders.IsFastDev);
    }

    [Fact]
    public async Task 離開FastDev_只還原FastDev服務並清旗標()
    {
        await MakeFastDevAsync(_api, "api-cid");

        await _vm.ToggleProjectFastDevCommand.ExecuteAsync(_vm.Projects[0]);

        var up = Assert.Single(_cli.UpCalls);
        Assert.Equal(["api"], up.Names);
        Assert.Null(up.OverridePath);
        Assert.False(_api.IsFastDev);
        Assert.False(File.Exists(FastDevOverrideStore.PathFor(_dir)));
    }

    [Fact]
    public async Task 全部啟動_選取服務都已在執行時給提示而非靜默()
    {
        // 不設 SelectedService：選取會觸發日誌串流；無選取 = 全部範圍
        await MakeFastDevAsync(_api, "api-cid");
        await MakeFastDevAsync(_orders, "orders-cid");
        _nginx.IsFastDev = true;
        _nginx.Status = ContainerStatus.Running;

        await _vm.AllUpCommand.ExecuteAsync(null);

        Assert.Empty(_cli.UpCalls);
        Assert.Contains("已在執行", _vm.StatusMessage);
    }

    // ───── 依賴展開 ─────

    private static DockerService Svc(string name, bool running = false, params string[] dependsOn) => new()
    {
        Name = name,
        DependsOn = [.. dependsOn],
        Status = running ? ContainerStatus.Running : ContainerStatus.Stopped,
    };

    [Fact]
    public void 展開依賴_加入未執行的依賴()
    {
        var api = Svc("api");
        var nginx = Svc("nginx", false, "api");

        var added = MainViewModel.ExpandDependencies([nginx], [nginx, api]);

        Assert.Equal([api], added);
    }

    [Fact]
    public void 展開依賴_遞迴帶起間接依賴()
    {
        var orders = Svc("orders");
        var api = Svc("api", false, "orders");
        var nginx = Svc("nginx", false, "api");

        var added = MainViewModel.ExpandDependencies([nginx], [nginx, api, orders]);

        Assert.Equal([api, orders], added);
    }

    [Fact]
    public void 展開依賴_已在跑的依賴不加入_但遞迴穿過它繼續看()
    {
        var orders = Svc("orders");
        var api = Svc("api", true, "orders");
        var nginx = Svc("nginx", false, "api");

        var added = MainViewModel.ExpandDependencies([nginx], [nginx, api, orders]);

        Assert.Equal([orders], added);
    }

    [Fact]
    public void 展開依賴_循環依賴不無限迴圈()
    {
        var orders = Svc("orders", false, "api");
        var api = Svc("api", false, "orders");

        var added = MainViewModel.ExpandDependencies([api], [api, orders]);

        Assert.Equal([orders], added);
    }

    [Fact]
    public void 展開依賴_找不到的依賴名稱略過_已在scope內的不重複加入()
    {
        var api = Svc("api");
        var nginx = Svc("nginx", false, "api", "redis");

        var added = MainViewModel.ExpandDependencies([nginx, api], [nginx, api]);

        Assert.Empty(added);
    }

    [Fact]
    public async Task 單服務啟動_自動帶起未執行的依賴並照常進FastDev()
    {
        _nginx.DependsOn = ["api"];

        await _vm.StartServicesAsync(_dir, [_nginx]);

        var up = Assert.Single(_cli.UpCalls);
        Assert.Equal(["nginx", "api"], up.Names);
        Assert.Equal([Path.Combine(_dir, "api", "api.csproj")], Assert.Single(_build.Calls));
        Assert.True(_api.IsFastDev);
        Assert.False(_nginx.IsFastDev);
    }

    [Fact]
    public async Task 單服務啟動_已在跑的依賴不動()
    {
        _nginx.DependsOn = ["api"];
        _api.Status = ContainerStatus.Running;
        _api.ContainerId = "api-cid";

        await _vm.StartServicesAsync(_dir, [_nginx]);

        Assert.Equal(["nginx"], Assert.Single(_cli.UpCalls).Names);
        Assert.Empty(_build.Calls);
    }

    [Fact]
    public async Task 完整重建_不展開依賴()
    {
        _nginx.DependsOn = ["api"];

        await _vm.StartServicesAsync(_dir, [_nginx], exitFastDev: true);

        Assert.Equal(["nginx"], Assert.Single(_cli.UpCalls).Names);
    }

    // ───── 容器重建後重啟依賴它的服務 ─────

    private ContainerInfo Container(string id, string service) => new()
    {
        ID = id,
        Names = $"proj-{service}-1",
        State = "running",
        Labels = $"com.docker.compose.service={service},com.docker.compose.project=proj," +
                 $"com.docker.compose.project.working_dir={_dir}",
    };

    private void SetupRunningNginxDependingOnApi()
    {
        _nginx.DependsOn = ["api"];
        _nginx.Status = ContainerStatus.Running;
        _nginx.ContainerId = "nginx-cid";
        _api.ContainerId = "old-api";
    }

    [Fact]
    public async Task 容器重建_重啟直接依賴它且在跑的服務()
    {
        SetupRunningNginxDependingOnApi();
        _cli.RunningContainers = [Container("new-api", "api")];

        var ok = await _vm.StartServicesAsync(_dir, [_api]);

        Assert.True(ok);
        Assert.Equal(["nginx-cid"], _cli.RestartCalls);
    }

    [Fact]
    public async Task 容器重建_間接依賴也重啟()
    {
        SetupRunningNginxDependingOnApi();
        var gateway = NewService("gateway");
        gateway.DependsOn = ["nginx"];
        gateway.Status = ContainerStatus.Running;
        gateway.ContainerId = "gw-cid";
        _vm.Projects[0].ComposeFiles[0].Services.Add(gateway);
        _cli.RunningContainers = [Container("new-api", "api")];

        await _vm.StartServicesAsync(_dir, [_api]);

        Assert.Equivalent(new[] { "nginx-cid", "gw-cid" }, _cli.RestartCalls);
    }

    [Fact]
    public async Task 容器未重建_不重啟依賴者()
    {
        SetupRunningNginxDependingOnApi();
        _cli.RunningContainers = [Container("old-api", "api")];

        await _vm.StartServicesAsync(_dir, [_api]);

        Assert.Empty(_cli.RestartCalls);
    }

    [Fact]
    public async Task 容器重建_依賴者沒在跑時不重啟()
    {
        SetupRunningNginxDependingOnApi();
        _nginx.Status = ContainerStatus.Stopped;
        _cli.RunningContainers = [Container("new-api", "api")];

        await _vm.StartServicesAsync(_dir, [_api]);

        Assert.Empty(_cli.RestartCalls);
    }

    [Fact]
    public async Task 容器重建_依賴者本身就在scope內時不另外重啟()
    {
        SetupRunningNginxDependingOnApi();
        _cli.RunningContainers = [Container("new-api", "api")];

        await _vm.StartServicesAsync(_dir, [_api, _nginx]);

        Assert.Empty(_cli.RestartCalls);
    }

    [Fact]
    public async Task 取不到容器清單_略過重啟步驟()
    {
        SetupRunningNginxDependingOnApi();
        _cli.RunningContainers = null;

        var ok = await _vm.StartServicesAsync(_dir, [_api]);

        Assert.True(ok);
        Assert.Empty(_cli.RestartCalls);
    }

    [Fact]
    public async Task 重啟依賴者失敗_只警告不算啟動失敗也不回滾()
    {
        SetupRunningNginxDependingOnApi();
        _cli.RunningContainers = [Container("new-api", "api")];
        _cli.RestartExitCode = 1;

        var ok = await _vm.StartServicesAsync(_dir, [_api]);

        Assert.True(ok);
        Assert.True(_api.IsFastDev);
        Assert.Contains("nginx", _vm.StatusMessage);
        Assert.Contains("重啟失敗", _vm.StatusMessage);
    }

    [Fact]
    public async Task 離開FastDev重建容器_同樣重啟依賴者()
    {
        await MakeFastDevAsync(_api, "old-api");
        SetupRunningNginxDependingOnApi();
        _cli.RunningContainers = [Container("new-api", "api")];

        await _vm.StartServicesAsync(_dir, [_api], exitFastDev: true);

        Assert.Equal(["nginx-cid"], _cli.RestartCalls);
    }

    // ───── 熱重載補跑 ─────

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), "等待逾時");
    }

    [Fact]
    public async Task 熱重載_操作進行中由watcher觸發_結束後補跑一次且去重()
    {
        await MakeFastDevAsync(_api, "api-cid");
        _vm.IsOperating = true;

        await _vm.OnFastDevSolutionChangedAsync(_dir);
        await _vm.OnFastDevSolutionChangedAsync(_dir);

        Assert.Empty(_build.Calls);

        _vm.IsOperating = false;

        await WaitUntilAsync(() => _build.Calls.Count >= 1);
        await Task.Delay(300);
        Assert.Single(_build.Calls);
    }

    [Fact]
    public async Task 熱重載補跑_目錄已無FastDev服務時略過()
    {
        await MakeFastDevAsync(_api, "api-cid");
        _vm.IsOperating = true;
        await _vm.OnFastDevSolutionChangedAsync(_dir);
        _api.IsFastDev = false;

        _vm.IsOperating = false;
        await Task.Delay(300);

        Assert.Empty(_build.Calls);
    }

    [Fact]
    public async Task 熱重載補跑_排空時仍忙碌就先不跑()
    {
        await MakeFastDevAsync(_api, "api-cid");
        _vm.IsOperating = true;
        await _vm.OnFastDevSolutionChangedAsync(_dir);

        _vm.IsOperating = false;
        _vm.IsOperating = true;
        await Task.Delay(300);

        Assert.Empty(_build.Calls);
    }

    // ───── Port 衝突只問一次 ─────

    private DockerProject AddRunningHolder(params (string Name, string Ports)[] services)
    {
        var compose = new ComposeFile { DirectoryPath = _dir };
        foreach (var (name, ports) in services)
        {
            var svc = NewService(name);
            svc.Ports = ports;
            svc.Status = ContainerStatus.Running;
            compose.Services.Add(svc);
        }
        var project = new DockerProject { Name = "holder", FolderPath = _dir };
        project.ComposeFiles.Add(compose);
        _vm.Projects.Add(project);
        return project;
    }

    private DockerProject AddTwoDirectoryProject(string port1, string port2)
    {
        var dir2 = Path.Combine(_root, "proj2");
        Directory.CreateDirectory(dir2);
        File.WriteAllText(Path.Combine(dir2, "docker-compose.yml"), "services: {}");

        var web = NewService("web");
        web.Ports = port1;
        var other = NewService("other");
        other.WorkingDirectory = dir2;
        other.ComposeFilePath = Path.Combine(dir2, "docker-compose.yml");
        other.Ports = port2;

        var composeA = new ComposeFile { DirectoryPath = _dir };
        composeA.Services.Add(web);
        var composeB = new ComposeFile { DirectoryPath = dir2 };
        composeB.Services.Add(other);
        var multi = new DockerProject { Name = "multi", FolderPath = _root };
        multi.ComposeFiles.Add(composeA);
        multi.ComposeFiles.Add(composeB);
        _vm.Projects.Add(multi);
        return multi;
    }

    [Fact]
    public async Task 批次啟動_跨目錄的Port衝突合併成一次詢問()
    {
        var holder = AddRunningHolder(("old1", "8080:80"), ("old2", "9090:80"));
        var multi = AddTwoDirectoryProject("8080:80", "9090:80");
        var asked = new List<List<(int Port, DockerService Wanted, DockerService Running)>>();
        _vm.PortConflictResolver = conflicts =>
        {
            asked.Add(conflicts);
            // 模擬使用者同意、衝突服務已停止
            foreach (var svc in holder.ComposeFiles[0].Services) svc.Status = ContainerStatus.Stopped;
            return Task.FromResult(true);
        };

        await _vm.ProjectUpCommand.ExecuteAsync(multi);

        var ask = Assert.Single(asked);
        Assert.Equivalent(new[] { 8080, 9090 }, ask.Select(c => c.Port));
        Assert.Equal(2, _cli.UpCalls.Count);
    }

    [Fact]
    public async Task 批次啟動_使用者拒絕Port衝突時整批中止()
    {
        AddRunningHolder(("old1", "8080:80"));
        var multi = AddTwoDirectoryProject("8080:80", "");
        _vm.PortConflictResolver = _ => Task.FromResult(false);

        await _vm.ProjectUpCommand.ExecuteAsync(multi);

        Assert.Empty(_cli.UpCalls);
    }

    // ───── 日誌 ─────

    [Fact]
    public void 檢視日誌_一律走composeLogs_即使已有ContainerId()
    {
        _api.ContainerId = "api-cid";

        _vm.ViewLogsCommand.Execute(_api);

        Assert.Equal([(_dir, "api")], _cli.ComposeLogCalls);
    }

    // ───── 專案環境（所有憑證皆為測試用假值）─────

    private const string FakeSecret = "fake-secret$for'test-only";

    private DockerProject Proj => _vm.Projects[0];

    private void WriteStageSettings(string serviceDir, string env = "Stage", string clientId = "fake-client")
    {
        Directory.CreateDirectory(Path.Combine(_dir, serviceDir));
        File.WriteAllText(Path.Combine(_dir, serviceDir, $"appsettings.{env}.json"),
            $$"""{ "AppRegistration": { "TenantId": "fake-tenant", "ClientId": "{{clientId}}", "ClientSecret": "{{FakeSecret}}" } }""");
    }

    [Fact]
    public async Task 預設環境Development_override不含AZURE()
    {
        await _vm.StartServicesAsync(_dir, [_api]);

        var text = Assert.Single(_cli.UpCalls).OverrideText!;
        Assert.Contains("ASPNETCORE_ENVIRONMENT=Development", text);
        Assert.DoesNotContain("AZURE_", text);
    }

    [Fact]
    public async Task Stage啟動_override含環境與AZURE_且錢號跳脫()
    {
        WriteStageSettings("api");
        Proj.SelectedEnvironment = "Stage";

        await _vm.StartServicesAsync(_dir, [_api]);

        var text = Assert.Single(_cli.UpCalls).OverrideText!;
        Assert.Contains("ASPNETCORE_ENVIRONMENT=Stage", text);
        Assert.Contains("AZURE_TENANT_ID=fake-tenant", text);
        Assert.Contains("AZURE_CLIENT_ID=fake-client", text);
        Assert.Contains("AZURE_CLIENT_SECRET=fake-secret$$for''test-only", text);
    }

    [Fact]
    public async Task Stage啟動_服務自己沒憑證時退到AuthenticationBackend()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "AuthenticationBackend"));
        File.WriteAllText(Path.Combine(_dir, "AuthenticationBackend", "AuthenticationBackend.csproj"), "<Project />");
        WriteStageSettings("AuthenticationBackend", clientId: "from-auth");
        Proj.SelectedEnvironment = "Stage";

        await _vm.StartServicesAsync(_dir, [_api]);

        Assert.Contains("AZURE_CLIENT_ID=from-auth", Assert.Single(_cli.UpCalls).OverrideText!);
    }

    [Fact]
    public async Task Stage啟動_找不到憑證時記錄警告且不寫AZURE_()
    {
        var logs = new List<string>();
        _vm.LogObserver = logs.Add;
        Proj.SelectedEnvironment = "Stage";

        await _vm.StartServicesAsync(_dir, [_api]);

        var text = Assert.Single(_cli.UpCalls).OverrideText!;
        Assert.Contains("ASPNETCORE_ENVIRONMENT=Stage", text);
        Assert.DoesNotContain("AZURE_", text);
        Assert.Contains(logs, l => l.Contains(EnvironmentCredentialResolver.NotFoundMessage));
    }

    [Fact]
    public async Task 日誌不含憑證值_compose回顯override內容時被遮蔽()
    {
        WriteStageSettings("api");
        Proj.SelectedEnvironment = "Stage";
        var logs = new List<string>();
        _vm.LogObserver = logs.Add;
        // 模擬 compose 解析失敗時把 override 的內容行原樣印出（含跳脫後與未跳脫兩種形式）
        _cli.EmitOutput = ["yaml: line 9: - 'AZURE_CLIENT_SECRET=fake-secret$$for''test-only'", "secret=fake-secret$for'test-only"];

        await _vm.StartServicesAsync(_dir, [_api]);

        Assert.DoesNotContain(logs, l => l.Contains("fake-secret"));
        Assert.Contains(logs, l => l.Contains("***"));
    }

    [Fact]
    public async Task Production啟動被拒絕_不up不build不改狀態()
    {
        Proj.SelectedEnvironment = "Production";
        _vm.ConfirmProductionEnvironment = _ => false;

        var ok = await _vm.StartServicesAsync(_dir, [_api]);

        Assert.False(ok);
        Assert.Empty(_cli.UpCalls);
        Assert.Empty(_build.Calls);
        Assert.False(_api.IsFastDev);
        Assert.Empty((await _settings.LoadAsync()).FastDevEnabledServiceKeys);
    }

    [Fact]
    public async Task Production啟動確認後_才up且只問一次()
    {
        Proj.SelectedEnvironment = "Production";
        var asked = 0;
        _vm.ConfirmProductionEnvironment = _ => { asked++; return true; };

        var ok = await _vm.StartServicesAsync(_dir, [_api]);

        Assert.True(ok);
        Assert.Equal(1, asked);
        Assert.Contains("ASPNETCORE_ENVIRONMENT=Production", Assert.Single(_cli.UpCalls).OverrideText!);
    }

    [Fact]
    public async Task 熱重載_Production不重問確認()
    {
        await MakeFastDevAsync(_api, "api-cid");
        Proj.SelectedEnvironment = "Production";
        _vm.ConfirmProductionEnvironment = _ => throw new InvalidOperationException("不該再問");

        await _vm.OnFastDevSolutionChangedAsync(_dir, _api);

        Assert.Equal(["api-cid"], _cli.RestartCalls);
    }

    [Fact]
    public async Task 離開FastDev_Production專案不問確認()
    {
        await MakeFastDevAsync(_api, "api-cid");
        Proj.SelectedEnvironment = "Production";
        _vm.ConfirmProductionEnvironment = _ => throw new InvalidOperationException("不該再問");

        var ok = await _vm.StartServicesAsync(_dir, [_api], exitFastDev: true);

        Assert.True(ok);
    }

    [Fact]
    public async Task 切到Production被拒絕_環境與設定都不變()
    {
        _vm.ConfirmProductionEnvironment = _ => false;

        await _vm.SelectProjectEnvironmentCommand.ExecuteAsync(new EnvironmentChoice(Proj, "Production", false));

        Assert.Equal("Development", Proj.SelectedEnvironment);
        Assert.Empty((await _settings.LoadAsync()).ProjectEnvironments);
    }

    [Fact]
    public async Task 切換環境_持久化_切回Development則移除項目()
    {
        await _vm.SelectProjectEnvironmentCommand.ExecuteAsync(new EnvironmentChoice(Proj, "Stage", false));

        Assert.Equal("Stage", Proj.SelectedEnvironment);
        Assert.Equal("Stage", Assert.Single((await _settings.LoadAsync()).ProjectEnvironments).Value);
        Assert.Empty(_cli.UpCalls);

        await _vm.SelectProjectEnvironmentCommand.ExecuteAsync(new EnvironmentChoice(Proj, "Development", false));

        Assert.Empty((await _settings.LoadAsync()).ProjectEnvironments);
    }

    [Fact]
    public async Task 切換環境_有服務在跑且使用者同意_重啟且不重問Production()
    {
        await MakeFastDevAsync(_api, "api-cid");
        var productionAsked = 0;
        _vm.ConfirmProductionEnvironment = _ => { productionAsked++; return true; };
        _vm.ConfirmRestartForEnvironment = _ => true;

        await _vm.SelectProjectEnvironmentCommand.ExecuteAsync(new EnvironmentChoice(Proj, "Production", false));

        Assert.Equal(1, productionAsked);
        var up = Assert.Single(_cli.UpCalls);
        Assert.Equal(["api"], up.Names);
        Assert.Contains("ASPNETCORE_ENVIRONMENT=Production", up.OverrideText!);
    }

    [Fact]
    public async Task 切換環境_有服務在跑但使用者不重啟_不自動重啟()
    {
        await MakeFastDevAsync(_api, "api-cid");
        _vm.ConfirmRestartForEnvironment = _ => false;

        await _vm.SelectProjectEnvironmentCommand.ExecuteAsync(new EnvironmentChoice(Proj, "Stage", false));

        Assert.Empty(_cli.UpCalls);
        Assert.Contains("重新啟動後生效", _vm.StatusMessage);
    }

    [Fact]
    public void 載入設定_缺項Development_Production未開放時回落並記錄()
    {
        var logs = new List<string>();
        _vm.LogObserver = logs.Add;
        var settings = new AppSettings
        {
            ProjectEnvironments = new() { [_dir.ToUpperInvariant()] = "Production" },
            AllowProductionEnvironment = false,
        };

        _vm.ApplyProjectEnvironments(settings);

        Assert.Equal("Development", Proj.SelectedEnvironment);
        Assert.Contains(logs, l => l.Contains("Production"));

        settings.AllowProductionEnvironment = true;
        _vm.ApplyProjectEnvironments(settings);
        Assert.Equal("Production", Proj.SelectedEnvironment);

        _vm.ApplyProjectEnvironments(new AppSettings());
        Assert.Equal("Development", Proj.SelectedEnvironment);
    }

    [Fact]
    public async Task 可選環境_依檔案偵測且包含目前選中的環境()
    {
        WriteStageSettings("api", "Stage");
        WriteStageSettings("orders", "Production");
        _vm.ApplyProjectEnvironments(new AppSettings { AllowProductionEnvironment = false });

        await _vm.RefreshAvailableEnvironmentsAsync();
        Assert.Equal(["Development", "Stage"], Proj.AvailableEnvironments);

        _vm.ApplyProjectEnvironments(new AppSettings { AllowProductionEnvironment = true });
        await _vm.RefreshAvailableEnvironmentsAsync();
        Assert.Equal(["Development", "Stage", "Production"], Proj.AvailableEnvironments);
        Assert.Equal(["Development", "Stage", "Production"], Proj.EnvironmentChoices.Select(c => c.Name));
    }

    private sealed record UpCall(string Directory, List<string> Names, string? OverridePath, string? OverrideText);

    private sealed class FakeHostBuild : HostBuildService
    {
        public List<List<string>> Calls { get; } = [];
        public int ExitCode { get; set; }

        public override Task<(int ExitCode, string Output)> BuildAsync(
            string? solutionPath, IReadOnlyList<string> serviceProjectFullPaths,
            Action<string> onOutput, CancellationToken ct)
        {
            Calls.Add([.. serviceProjectFullPaths]);
            return Task.FromResult((ExitCode, string.Empty));
        }
    }

    private sealed class FakeGit : IGitService
    {
        public bool IsGitRepository(string folderPath) => false;
        public Task<string> GetCurrentBranchAsync(string folderPath) => Task.FromResult("");
        public Task<List<string>> GetLocalBranchesAsync(string folderPath) => Task.FromResult(new List<string>());
        public Task<List<string>> GetRemoteBranchesAsync(string folderPath) => Task.FromResult(new List<string>());
        public Task<bool> IsDirtyAsync(string folderPath) => Task.FromResult(false);
        public Task<(bool Success, string Output)> CheckoutAsync(string folderPath, string branchName) =>
            Task.FromResult((true, ""));
        public Task<GitPullResult> PullAsync(string folderPath, string remoteBranch) =>
            Task.FromResult(new GitPullResult(true, "", false, true, null, null));
    }

    private sealed class FakeCli : IDockerCliService
    {
        public List<UpCall> UpCalls { get; } = [];
        public List<string> RestartCalls { get; } = [];
        public List<(string Dir, string Name)> ComposeLogCalls { get; } = [];
        public List<ContainerInfo>? RunningContainers { get; set; } = [];
        public int UpExitCode { get; set; }
        public int RestartExitCode { get; set; }
        // 模擬 compose 在 up 過程輸出的行（走 onOutput，等同寫進操作日誌）
        public string[] EmitOutput { get; set; } = [];

        public bool UseComposeV2 { get; set; } = true;
        public DockerMode DockerMode { get; set; }
        public string WslDistroName { get; set; } = "";
        public int BuildKitParallelism { get; set; }

        public Task<bool> IsDockerAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<List<ContainerInfo>?> GetRunningContainersAsync(CancellationToken ct = default) =>
            Task.FromResult(RunningContainers);

        public Task<(int ExitCode, string Output)> ComposeUpServicesAsync(
            string workingDirectory, IReadOnlyList<string> serviceNames, Action<string> onOutput,
            CancellationToken ct, string? extraOverrideFile = null)
        {
            // override 檔會被後續操作覆寫／刪除，呼叫當下就讀內容
            var text = extraOverrideFile != null ? File.ReadAllText(extraOverrideFile) : null;
            UpCalls.Add(new UpCall(workingDirectory, [.. serviceNames], extraOverrideFile, text));
            foreach (var line in EmitOutput) onOutput(line);
            return Task.FromResult((UpExitCode, string.Empty));
        }

        public Task<(int ExitCode, string Output)> RestartContainerAsync(
            string containerNameOrId, Action<string> onOutput, CancellationToken ct)
        {
            lock (RestartCalls) RestartCalls.Add(containerNameOrId);
            return Task.FromResult((RestartExitCode, string.Empty));
        }

        public ProcessStream StartComposeLogStream(string workingDirectory, string serviceName)
        {
            ComposeLogCalls.Add((workingDirectory, serviceName));
            // ViewLogs 會讀串流，給一個立刻結束的真實行程
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c echo hi")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            return new ProcessStream(ProcessLauncher.Start(psi));
        }
        public ProcessStream StartComposeWatch(string workingDirectory, IEnumerable<string> serviceNames) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> ComposeDownWithLogAsync(string workingDirectory, Action<string> onOutput, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> ComposeStopWithLogAsync(string workingDirectory, Action<string> onOutput, string? serviceName = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GetContainerLogsAsync(string containerNameOrId, int tail = 30, CancellationToken ct = default) => throw new NotSupportedException();
        public ProcessStream StartDockerEvents() => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> DockerImagePruneAsync(bool all, Action<string> onOutput, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> DockerVolumePruneAsync(Action<string> onOutput, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(int ExitCode, string Output)> DockerSystemPruneAsync(bool all, bool includeVolumes, Action<string> onOutput, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
