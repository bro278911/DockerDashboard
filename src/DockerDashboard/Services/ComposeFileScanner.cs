using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DockerDashboard.Models;

namespace DockerDashboard.Services;

public class ComposeFileScanner
{
    private static readonly string[] MainComposeFileNames =
    [
        "docker-compose.yml",
        "docker-compose.yaml",
        "compose.yml",
        "compose.yaml"
    ];

    public List<ComposeFile> ScanFolder(string folderPath)
    {
        var results = new List<ComposeFile>();

        if (!Directory.Exists(folderPath))
            return results;

        // 掃描所有子目錄中的主 compose 檔案
        foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            if (MainComposeFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                // 跳過 obj/ bin/ node_modules/ 等目錄
                var relativePath = Path.GetRelativePath(folderPath, file);
                if (ShouldSkipPath(relativePath))
                    continue;

                var composeFile = ParseWithDockerCli(file);
                composeFile ??= ParseManually(file);

                if (composeFile != null)
                    results.Add(composeFile);
            }
        }

        return results;
    }

    // docker compose config --format json 自動解析變數、anchor、merge key、多檔合併
    private ComposeFile? ParseWithDockerCli(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath)!;

            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("compose");
            foreach (var arg in GetComposeFileArgs(directory))
                psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add("config");
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("json");

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());
            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);
            stderrTask.Wait(1000);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                return null;

            return ParseJsonConfig(json, filePath, directory);
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
            DirectoryPath = directory
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

            // 解析 ports（docker compose config 的格式）
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

            var yaml = new YamlDotNet.RepresentationModel.YamlStream();
            using var reader = new StreamReader(filePath);
            yaml.Load(reader);

            if (yaml.Documents.Count == 0)
                return compose;

            var root = (YamlDotNet.RepresentationModel.YamlMappingNode)yaml.Documents[0].RootNode;

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

    private static List<string> GetComposeFileArgs(string directory)
    {
        var fileArgs = new List<string>();

        var mainFiles = new[] { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" };
        var mainFile = mainFiles.FirstOrDefault(f => File.Exists(Path.Combine(directory, f)));
        if (mainFile == null) return fileArgs;

        fileArgs.Add("-f");
        fileArgs.Add(mainFile);

        var overrideFiles = new[] { "docker-compose.override.yml", "docker-compose.override.yaml" };
        var overrideFile = overrideFiles.FirstOrDefault(f => File.Exists(Path.Combine(directory, f)));
        if (overrideFile != null)
        {
            fileArgs.Add("-f");
            fileArgs.Add(overrideFile);
        }

        var buildFiles = new[] { "docker-compose.build.yml", "docker-compose.build.yaml" };
        var buildFile = buildFiles.FirstOrDefault(f => File.Exists(Path.Combine(directory, f)));
        if (buildFile != null)
        {
            fileArgs.Add("-f");
            fileArgs.Add(buildFile);
        }

        return fileArgs;
    }

    // 清理 compose 變數語法，例如 ${DOCKER_REGISTRY:-}nginx → nginx
    private static string CleanImageName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        // 處理 ${VAR:-default}value 或 ${VAR:-}value 格式
        while (raw.Contains("${"))
        {
            var start = raw.IndexOf("${", StringComparison.Ordinal);
            var end = raw.IndexOf("}", start, StringComparison.Ordinal);
            if (end < 0) break;

            // 取得預設值部分
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

    private static bool ShouldSkipPath(string relativePath)
    {
        var segments = relativePath.Split([Path.DirectorySeparatorChar, '/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(seg => SkipDirs.Contains(seg));
    }
}
