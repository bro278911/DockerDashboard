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
        sb.AppendLine($"    image: {config.RuntimeImage}");
        sb.AppendLine("    volumes:");
        sb.AppendLine($"      - {projectDirHostPath}:/app:rw");
        sb.AppendLine($"      - {nugetHostPath}:/root/.nuget/packages:ro");
        sb.AppendLine("    environment:");
        sb.AppendLine("      - ASPNETCORE_ENVIRONMENT=Development");
        sb.AppendLine($"    entrypoint: [\"dotnet\", \"{dll}\", \"--additionalProbingPath\", \"/root/.nuget/packages\"]");
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

    // 整包 up：docker compose -f 原檔... -f fastdev up -d（不帶 service、不 --no-deps）
    public static List<string> BuildUpAllArgs(
        IEnumerable<string> composePrefixArgs, IEnumerable<string> composeFileArgs, string? extraOverrideFile)
    {
        var args = new List<string>();
        args.AddRange(composePrefixArgs);
        args.AddRange(composeFileArgs);
        if (!string.IsNullOrEmpty(extraOverrideFile))
        {
            args.Add("-f");
            args.Add(extraOverrideFile);
        }
        args.Add("up");
        args.Add("-d");
        return args;
    }
}
