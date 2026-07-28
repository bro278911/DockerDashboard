using System;
using System.IO;
using System.Linq;

namespace DockerDashboard.Services;

public static class FastDevOverrideStore
{
    private static string Dir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DockerDashboard", "fastdev");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string PathFor(string serviceKey)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(serviceKey.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return Path.Combine(Dir, safe + ".yml");
    }

    public static string Write(string serviceKey, string yaml)
    {
        var path = PathFor(serviceKey);
        File.WriteAllText(path, yaml);
        return path;
    }

    public static void Delete(string serviceKey)
    {
        var path = PathFor(serviceKey);
        if (File.Exists(path)) File.Delete(path);
    }
}
