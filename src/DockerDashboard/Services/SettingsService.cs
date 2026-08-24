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
            return _cached = await ReadAsync();
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
            await WriteAsync(settings);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 在鎖內完成「取設定 → 變更 → 落檔」。呼叫端拿到的是共用的 cached 物件，
    /// 若在鎖外變更，序列化可能讀到與呼叫端預期不同的混合狀態；用本方法可讓變更與
    /// 序列化位於同一個臨界區
    /// </summary>
    public async Task UpdateAsync(Action<AppSettings> mutate)
    {
        await _gate.WaitAsync();
        try
        {
            var settings = _cached ??= await ReadAsync();
            mutate(settings);
            await WriteAsync(settings);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AppSettings> ReadAsync()
    {
        if (!File.Exists(_settingsPath)) return new AppSettings();

        var json = await File.ReadAllTextAsync(_settingsPath);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }

    private async Task WriteAsync(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        // 暫存檔名加上唯一後綴：固定名稱時，同時開兩個 Dashboard 會互相覆蓋或搬走對方的暫存檔，
        // 造成 File.Move 失敗，或把另一個 writer 的內容發布成正式設定
        var tempPath = $"{_settingsPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }
}
