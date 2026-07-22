using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DockerDashboard.Models;

namespace DockerDashboard.Services;

public class ComposeFileScanner
{
    public DockerMode DockerMode { get; set; } = DockerMode.DockerDesktop;
    public string WslDistroName { get; set; } = "Ubuntu";

    private static readonly string[] MainComposeFileNames =
    [
        "docker-compose.yml",
        "docker-compose.yaml",
        "compose.yml",
        "compose.yaml"
    ];

    private readonly ScanCacheService _cache;

    public ComposeFileScanner(ScanCacheService cache) => _cache = cache;

    public async Task<List<ComposeFile>> ScanFolderAsync(string folderPath, bool useCache = true)
    {
        if (!Directory.Exists(folderPath))
            return [];

        await _cache.LoadAsync();

        var directories = EnumerateComposeDirectories(folderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => FindMainComposeFile(d) != null)
            .ToList();

        // docker compose config 每次 spawn 0.5~2 秒，目錄間平行化
        using var semaphore = new SemaphoreSlim(4);
        var results = await Task.WhenAll(directories.Select(async directory =>
        {
            var stamps = ScanCacheService.GetStamps(directory);
            if (useCache && _cache.TryGet(directory, stamps) is { } cached)
                return cached;

            await semaphore.WaitAsync();
            try
            {
                var mainComposePath = FindMainComposeFile(directory)!;
                var cliParsed = await ParseWithDockerCliAsync(mainComposePath);
                var composeFile = cliParsed ?? ParseManually(mainComposePath);
                // fallback 解析無變數展開與 override 合併，ProjectName 不可靠：
                // 不寫快取，待 Docker CLI 可用時重新以 compose config 解析
                if (cliParsed != null)
                    _cache.Store(directory, stamps, cliParsed);
                return composeFile;
            }
            finally
            {
                semaphore.Release();
            }
        }));

        await _cache.SaveAsync();
        return results.OfType<ComposeFile>().ToList();
    }

    // docker compose config --format json 自動解析變數、anchor、merge key、多檔合併
    private async Task<ComposeFile?> ParseWithDockerCliAsync(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath)!;

            ProcessStartInfo psi;
            if (DockerMode == DockerMode.Wsl2)
            {
                psi = new ProcessStartInfo
                {
                    FileName = "wsl",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(WslDistroName);
                psi.ArgumentList.Add("--cd");
                psi.ArgumentList.Add(DockerCliService.ConvertToWslPath(directory));
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add("docker");
                psi.ArgumentList.Add("compose");
                foreach (var arg in ComposeFileHelper.GetComposeFileArgs(directory))
                    psi.ArgumentList.Add(arg);
                psi.ArgumentList.Add("config");
                psi.ArgumentList.Add("--format");
                psi.ArgumentList.Add("json");
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "docker",
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("compose");
                foreach (var arg in ComposeFileHelper.GetComposeFileArgs(directory))
                    psi.ArgumentList.Add(arg);
                psi.ArgumentList.Add("config");
                psi.ArgumentList.Add("--format");
                psi.ArgumentList.Add("json");
            }

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
                var json = await stdoutTask;
                await stderrTask;
                await process.WaitForExitAsync(cts.Token);

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                    return null;

                return ParseJsonConfig(json, filePath, directory);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private ComposeFile? ParseJsonConfig(string json, string filePath, string directory)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("services", out var servicesElement))
            return null;

        var compose = new ComposeFile
        {
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            DirectoryPath = directory,
            ProjectName = root.TryGetProperty("name", out var nameEl)
                    && nameEl.GetString() is { } name && !string.IsNullOrWhiteSpace(name)
                ? name
                : DeriveDefaultProjectName(directory)
        };

        foreach (var service in servicesElement.EnumerateObject())
        {
            var dockerService = new DockerService
            {
                Name = service.Name,
                ComposeFilePath = filePath,
                WorkingDirectory = directory
            };

            if (service.Value.TryGetProperty("image", out var imageEl))
                dockerService.Image = imageEl.GetString() ?? string.Empty;

            if (service.Value.TryGetProperty("container_name", out var cnEl))
                dockerService.ContainerName = cnEl.GetString() ?? string.Empty;

            if (service.Value.TryGetProperty("ports", out var portsEl) && portsEl.ValueKind == JsonValueKind.Array)
            {
                var portStrings = new List<string>();
                foreach (var port in portsEl.EnumerateArray())
                {
                    var target = port.TryGetProperty("target", out var t) ? t.ToString() : "";
                    var published = "";
                    if (port.TryGetProperty("published", out var p))
                    {
                        published = p.ValueKind == JsonValueKind.String
                            ? p.GetString() ?? ""
                            : p.ToString();
                    }
                    if (!string.IsNullOrEmpty(published) && published != "0")
                        portStrings.Add($"{published}:{target}");
                    else if (!string.IsNullOrEmpty(target))
                        portStrings.Add(target);
                }
                dockerService.Ports = string.Join(", ", portStrings);
            }

            compose.Services.Add(dockerService);
        }

        return compose;
    }

    // fallback：docker CLI 不可用時用 YamlDotNet 手動解析
    private ComposeFile? ParseManually(string filePath)
    {
        try
        {
            var compose = new ComposeFile
            {
                FileName = Path.GetFileName(filePath),
                FilePath = filePath,
                DirectoryPath = Path.GetDirectoryName(filePath) ?? string.Empty
            };
            compose.ProjectName = DeriveDefaultProjectName(compose.DirectoryPath);

            var yaml = new YamlDotNet.RepresentationModel.YamlStream();
            using var reader = new StreamReader(filePath);
            yaml.Load(reader);

            if (yaml.Documents.Count == 0)
                return compose;

            var root = (YamlDotNet.RepresentationModel.YamlMappingNode)yaml.Documents[0].RootNode;

            // 含 ${...} 的 name 需要變數展開，fallback 無法解析，維持資料夾衍生預設值
            if (root.Children.TryGetValue(
                    new YamlDotNet.RepresentationModel.YamlScalarNode("name"), out var nameNode)
                && nameNode is YamlDotNet.RepresentationModel.YamlScalarNode nameScalar
                && !string.IsNullOrWhiteSpace(nameScalar.Value)
                && !nameScalar.Value.Contains("${"))
            {
                compose.ProjectName = nameScalar.Value;
            }

            if (root.Children.TryGetValue(
                    new YamlDotNet.RepresentationModel.YamlScalarNode("services"), out var servicesNode)
                && servicesNode is YamlDotNet.RepresentationModel.YamlMappingNode servicesMap)
            {
                foreach (var service in servicesMap.Children)
                {
                    var serviceName = ((YamlDotNet.RepresentationModel.YamlScalarNode)service.Key).Value ?? "unknown";
                    var dockerService = new DockerService
                    {
                        Name = serviceName,
                        ComposeFilePath = filePath,
                        WorkingDirectory = compose.DirectoryPath
                    };

                    if (service.Value is YamlDotNet.RepresentationModel.YamlMappingNode serviceConfig)
                    {
                        if (serviceConfig.Children.TryGetValue(
                                new YamlDotNet.RepresentationModel.YamlScalarNode("image"), out var imageNode))
                        {
                            var rawImage = ((YamlDotNet.RepresentationModel.YamlScalarNode)imageNode).Value ?? "";
                            dockerService.Image = CleanImageName(rawImage);
                        }

                        if (serviceConfig.Children.TryGetValue(
                                new YamlDotNet.RepresentationModel.YamlScalarNode("container_name"), out var cnNode))
                        {
                            dockerService.ContainerName =
                                ((YamlDotNet.RepresentationModel.YamlScalarNode)cnNode).Value ?? string.Empty;
                        }
                    }

                    compose.Services.Add(dockerService);
                }
            }

            return compose;
        }
        catch
        {
            return null;
        }
    }

    // compose 預設 project name 規則：資料夾名小寫化，僅保留 [a-z0-9_-]
    internal static string DeriveDefaultProjectName(string directory)
    {
        var baseName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)).ToLowerInvariant();
        var sb = new System.Text.StringBuilder(baseName.Length);
        foreach (var ch in baseName)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')
                sb.Append(ch);
        }
        return sb.ToString().TrimStart('_', '-');
    }

    // 清理 compose 變數語法，例如 ${DOCKER_REGISTRY:-}nginx → nginx
    private static string CleanImageName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        while (raw.Contains("${"))
        {
            var start = raw.IndexOf("${", StringComparison.Ordinal);
            var end = raw.IndexOf("}", start, StringComparison.Ordinal);
            if (end < 0) break;

            var varContent = raw.Substring(start + 2, end - start - 2);
            var defaultValue = "";
            var dashIdx = varContent.IndexOf(":-", StringComparison.Ordinal);
            if (dashIdx >= 0)
                defaultValue = varContent[(dashIdx + 2)..];

            raw = raw[..start] + defaultValue + raw[(end + 1)..];
        }

        return raw;
    }

    private static readonly HashSet<string> SkipDirs = new(
        ["obj", "bin", "node_modules", ".git", "packages"],
        StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateComposeDirectories(string rootPath)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            if (FindMainComposeFile(current) != null)
                yield return current;

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                if (SkipDirs.Contains(Path.GetFileName(child)))
                    continue;
                pending.Push(child);
            }
        }
    }

    private static string? FindMainComposeFile(string directory)
    {
        foreach (var name in MainComposeFileNames)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
