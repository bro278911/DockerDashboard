using System.Collections.Generic;
using System.Text;
using DockerDashboard.Models;

namespace DockerDashboard.Services;

public static class FastDevComposeGenerator
{
    public static string ContainerProjectPath(FastDevConfig config) =>
        "/src/" + config.CsprojRelativePath;

    public static string ContainerWorkingDir(FastDevConfig config)
    {
        var slash = config.CsprojRelativePath.LastIndexOf('/');
        var dir = slash < 0 ? string.Empty : config.CsprojRelativePath[..slash];
        return dir.Length == 0 ? "/src" : "/src/" + dir;
    }

    public static string GenerateOverrideYaml(
        string serviceName, FastDevConfig config, string srcRootHostPath, string nugetHostPath)
    {
        var projectPath = ContainerProjectPath(config);
        var sb = new StringBuilder();
        sb.AppendLine("services:");
        sb.AppendLine($"  {serviceName}:");
        sb.AppendLine($"    image: {config.RuntimeImage}");
        sb.AppendLine("    user: root");
        sb.AppendLine($"    working_dir: {ContainerWorkingDir(config)}");
        sb.AppendLine("    volumes:");
        sb.AppendLine($"      - {srcRootHostPath}:/src:rw");
        sb.AppendLine($"      - {nugetHostPath}:/root/.nuget/packages:rw");
        sb.AppendLine("    environment:");
        sb.AppendLine("      - DOTNET_USE_POLLING_FILE_WATCHER=1");
        sb.AppendLine("      - ASPNETCORE_ENVIRONMENT=Development");
        sb.AppendLine($"    entrypoint: [\"dotnet\", \"watch\", \"--non-interactive\", \"--project\", \"{projectPath}\", \"run\", \"--no-launch-profile\"]");
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
