using System.Collections.Generic;
using System.IO;

namespace DockerDashboard.Services;

internal static class ComposeFileHelper
{
    private static readonly string[] MainFiles =
        ["docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml"];

    private static readonly string[] OverrideFiles =
        ["docker-compose.override.yml", "docker-compose.override.yaml"];

    private static readonly string[] BuildFiles =
        ["docker-compose.build.yml", "docker-compose.build.yaml"];

    public static List<string> GetComposeFileArgs(string directory)
    {
        var fileArgs = new List<string>();
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return fileArgs;

        var mainFile = System.Array.Find(MainFiles, f => File.Exists(Path.Combine(directory, f)));
        if (mainFile == null) return fileArgs;

        fileArgs.Add("-f");
        fileArgs.Add(mainFile);

        var overrideFile = System.Array.Find(OverrideFiles, f => File.Exists(Path.Combine(directory, f)));
        if (overrideFile != null)
        {
            fileArgs.Add("-f");
            fileArgs.Add(overrideFile);
        }

        var buildFile = System.Array.Find(BuildFiles, f => File.Exists(Path.Combine(directory, f)));
        if (buildFile != null)
        {
            fileArgs.Add("-f");
            fileArgs.Add(buildFile);
        }

        return fileArgs;
    }
}
