using System;
using System.IO;
using System.Text.Json;
using System.Threading;
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
    private readonly SemaphoreSlim _writeLock = new(1, 1);
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

    // 多個獨立的非同步命令（匯入資料夾、加入/編輯/移除前端專案…）可能同時存檔，
    // 直接對同一檔案 WriteAllTextAsync 會有共用違規、後寫覆蓋前寫、中斷留下截斷 JSON 等風險。
    // 故以 SemaphoreSlim 序列化寫入，並先寫暫存檔再原子替換
    public async Task SaveAsync(AppSettings settings)
    {
        _cached = settings;
        var json = JsonSerializer.Serialize(settings, JsonOptions);

        await _writeLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var tempPath = _settingsPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
