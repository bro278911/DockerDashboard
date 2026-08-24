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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings? _cached;

    public SettingsService() : this(DefaultSettingsPath) { }

    internal SettingsService(string settingsPath) => _settingsPath = settingsPath;

    // 首次讀檔同樣進鎖：否則可能與 SaveAsync 的原子替換（File.Move）撞成共用違規，
    // 且較慢的讀取可能在存檔後才回來、用舊內容覆寫 _cached，讓後續存檔遺失變更
    public async Task<AppSettings> LoadAsync()
    {
        if (_cached != null) return _cached;

        await _gate.WaitAsync();
        try
        {
            if (_cached != null) return _cached;

            if (!File.Exists(_settingsPath))
                return _cached = new AppSettings();

            var json = await File.ReadAllTextAsync(_settingsPath);
            return _cached = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    // 多個獨立的非同步命令（匯入資料夾、加入/編輯/移除前端專案…）可能同時存檔，
    // 直接對同一檔案 WriteAllTextAsync 會有共用違規、後寫覆蓋前寫、中斷留下截斷 JSON 等風險。
    // 故以號誌序列化，並先寫暫存檔再原子替換。
    // 序列化與 _cached 更新必須在鎖內取快照：放在鎖外的話，等待中的寫入者會拿著舊 JSON，
    // 在較新的寫入完成後才落檔，把新狀態蓋掉
    public async Task SaveAsync(AppSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            _cached = settings;
            var json = JsonSerializer.Serialize(settings, JsonOptions);

            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var tempPath = _settingsPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
