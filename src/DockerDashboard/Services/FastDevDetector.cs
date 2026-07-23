using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DockerDashboard.Services;

public sealed record FastDevDetectionResult(IReadOnlyList<string> CsprojCandidates, string SdkImage);

public static class FastDevDetector
{
    private static readonly Regex SdkFromRegex = new(
        @"FROM\s+(mcr\.microsoft\.com/dotnet/sdk:[^\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static FastDevDetectionResult Detect(
        string workingDirectory, string serviceName, string defaultSdkImage)
    {
        if (!Directory.Exists(workingDirectory))
            return new FastDevDetectionResult([], defaultSdkImage);

        var allCsproj = Directory
            .EnumerateFiles(workingDirectory, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !IsInIgnoredDir(Path.GetRelativePath(workingDirectory, p)))
            .Select(p => ToPosixRelative(workingDirectory, p))
            .ToList();

        var nameMatched = allCsproj
            .Where(rel => rel.Split('/')[0].Equals(serviceName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var candidates = nameMatched.Count == 1 ? nameMatched : allCsproj;

        var sdkImage = DetectSdkImage(workingDirectory, candidates, defaultSdkImage);
        return new FastDevDetectionResult(candidates, sdkImage);
    }

    private static bool IsInIgnoredDir(string relativePath)
    {
        var parts = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p =>
            p.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string ToPosixRelative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private static string DetectSdkImage(
        string workingDirectory, IReadOnlyList<string> candidates, string defaultSdkImage)
    {
        foreach (var rel in candidates)
        {
            var projectDir = Path.GetDirectoryName(Path.Combine(workingDirectory, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (projectDir == null) continue;
            var dockerfile = Path.Combine(projectDir, "Dockerfile");
            if (!File.Exists(dockerfile)) continue;
            var match = SdkFromRegex.Match(File.ReadAllText(dockerfile));
            if (match.Success) return match.Groups[1].Value;
        }
        return defaultSdkImage;
    }
}
