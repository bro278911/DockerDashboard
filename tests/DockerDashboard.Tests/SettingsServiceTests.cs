using System.IO;
using System.Text.Json;
using DockerDashboard.Models;
using DockerDashboard.Services;

namespace DockerDashboard.Tests;

public class SettingsServiceTests
{
    // 多個獨立命令可能同時存檔；序列化寫入 + 原子替換後，檔案不該出現截斷或無效 JSON
    [Fact]
    public async Task SaveAsync_併發存檔時檔案內容仍為完整JSON()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dd-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");

        try
        {
            var service = new SettingsService(path);

            var saves = Enumerable.Range(0, 20).Select(i => service.SaveAsync(new AppSettings
            {
                ImportedFolders = [.. Enumerable.Range(0, 50).Select(n => $@"D:\repo\proj-{i}-{n}")],
            }));
            await Task.WhenAll(saves);

            var json = await File.ReadAllTextAsync(path);
            var restored = JsonSerializer.Deserialize<AppSettings>(json);

            Assert.NotNull(restored);
            Assert.Equal(50, restored!.ImportedFolders.Count);
            Assert.Empty(Directory.EnumerateFiles(dir, "settings.json.*.tmp")); // 暫存檔不應殘留
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_回傳快照_呼叫端修改不會污染快取()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dd-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");

        try
        {
            var service = new SettingsService(path);
            await service.SaveAsync(new AppSettings { ImportedFolders = ["A"] });

            var first = await service.LoadAsync();
            first.ImportedFolders.Add("B"); // 未經 Save/Update 的外部修改不應反寫到快取

            var second = await service.LoadAsync();
            Assert.Equal(["A"], second.ImportedFolders);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
