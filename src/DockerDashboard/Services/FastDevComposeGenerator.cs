using System.Collections.Generic;
using System.Text;
using DockerDashboard.Models;

namespace DockerDashboard.Services;

public static class FastDevComposeGenerator
{
    public static string ContainerAppDir() => "/app";

    public static string ProjectDirRelative(FastDevConfig config)
    {
        var slash = config.CsprojRelativePath.LastIndexOf('/');
        return slash < 0 ? string.Empty : config.CsprojRelativePath[..slash];
    }

    public static string ContainerDllPath(FastDevConfig config) =>
        $"/app/bin/Debug/{config.Tfm}/{config.AssemblyName}.dll";

    public static string GenerateOverrideYaml(
        string serviceName, FastDevConfig config, string projectDirHostPath, string nugetHostPath)
    {
        var dll = ContainerDllPath(config);
        var sb = new StringBuilder();
        sb.AppendLine("services:");
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

    public static List<string> BuildUpArgs(
        IEnumerable<string> composePrefixArgs, IEnumerable<string> composeFileArgs,
        string? extraOverrideFile, string serviceName)
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
        args.Add("--no-deps");
        args.Add(serviceName);
        return args;
    }
}
