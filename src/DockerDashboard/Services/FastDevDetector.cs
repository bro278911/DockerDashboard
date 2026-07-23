using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DockerDashboard.Services;

public sealed record FastDevDetectionResult(
    IReadOnlyList<string> CsprojCandidates, string RuntimeImage, string? SolutionPath);

public sealed record FastDevProjectInfo(string Tfm, string AssemblyName);

public sealed record FastDevDockerfileInfo(string CsprojRelativePath, string RuntimeImage);

public static class FastDevDetector
{
    private static readonly Regex AspnetFromRegex = new(
        @"FROM\s+(mcr\.microsoft\.com/dotnet/aspnet:[^\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TfmRegex = new(
        @"<TargetFramework>\s*([^<\s]+)\s*</TargetFramework>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AssemblyNameRegex = new(
        @"<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static FastDevDetectionResult Detect(
        string workingDirectory, string serviceName, string defaultRuntimeImage)
    {
        if (!Directory.Exists(workingDirectory))
            return new FastDevDetectionResult([], defaultRuntimeImage, null);

        var allCsproj = Directory
            .EnumerateFiles(workingDirectory, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !IsInIgnoredDir(Path.GetRelativePath(workingDirectory, p)))
            .Select(p => ToPosixRelative(workingDirectory, p))
            .ToList();

        var nameMatched = allCsproj
            .Where(rel => rel.Split('/')[0].Equals(serviceName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var candidates = nameMatched.Count == 1 ? nameMatched : allCsproj;
        var runtimeImage = DetectRuntimeImage(workingDirectory, candidates, defaultRuntimeImage);
        var solution = Directory
            .EnumerateFiles(workingDirectory, "*.sln", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        return new FastDevDetectionResult(candidates, runtimeImage, solution);
    }

    // 對齊 VS：由 compose build.dockerfile 確定專案，取該 Dockerfile 同目錄的 csproj 與 aspnet runtime
    public static FastDevDockerfileInfo? FromDockerfile(
        string workingDirectory, string dockerfileAbsPath, string defaultRuntimeImage)
    {
        if (string.IsNullOrEmpty(dockerfileAbsPath) || !File.Exists(dockerfileAbsPath))
            return null;

        var projectDir = Path.GetDirectoryName(dockerfileAbsPath);
        if (projectDir == null || !Directory.Exists(projectDir))
            return null;

        var csproj = Directory.EnumerateFiles(projectDir, "*.csproj").FirstOrDefault();
        if (csproj == null)
            return null;

        var csprojRel = Path.GetRelativePath(workingDirectory, csproj).Replace('\\', '/');
        var runtime = defaultRuntimeImage;
        var match = AspnetFromRegex.Match(File.ReadAllText(dockerfileAbsPath));
        if (match.Success) runtime = match.Groups[1].Value;

        return new FastDevDockerfileInfo(csprojRel, runtime);
    }

    public static FastDevProjectInfo ReadProjectInfo(
        string workingDirectory, string csprojRelativePath, string defaultTfm)
    {
        var full = Path.Combine(workingDirectory, csprojRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var tfm = defaultTfm;
        var assembly = Path.GetFileNameWithoutExtension(full);
        if (File.Exists(full))
        {
            var text = File.ReadAllText(full);
            var tfmMatch = TfmRegex.Match(text);
            if (tfmMatch.Success) tfm = tfmMatch.Groups[1].Value;
            var asmMatch = AssemblyNameRegex.Match(text);
            if (asmMatch.Success) assembly = asmMatch.Groups[1].Value;
        }
        return new FastDevProjectInfo(tfm, assembly);
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

    private static string DetectRuntimeImage(
        string workingDirectory, IReadOnlyList<string> candidates, string defaultRuntimeImage)
    {
        foreach (var rel in candidates)
        {
            var projectDir = Path.GetDirectoryName(
                Path.Combine(workingDirectory, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (projectDir == null) continue;
            var dockerfile = Path.Combine(projectDir, "Dockerfile");
            if (!File.Exists(dockerfile)) continue;
            var match = AspnetFromRegex.Match(File.ReadAllText(dockerfile));
            if (match.Success) return match.Groups[1].Value;
        }
        return defaultRuntimeImage;
    }
}
