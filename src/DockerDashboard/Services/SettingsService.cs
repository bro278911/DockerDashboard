using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DockerDashboard.Models;

namespace DockerDashboard.Services;

public class SettingsService
{
    private static readonly string DefaultSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DockerDashboard",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private AppSettings? _cached;

    public SettingsService() : this(DefaultSettingsPath) { }

    internal SettingsService(string settingsPath) => _settingsPath = settingsPath;

    public async Task<AppSettings> LoadAsync()
    {
        if (_cached != null) return _cached;

        if (!File.Exists(_settingsPath))
            return _cached = new AppSettings();

        var json = await File.ReadAllTextAsync(_settingsPath);
        return _cached = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings)
    {
        _cached = settings;
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        await File.WriteAllTextAsync(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
