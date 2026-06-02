using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DockerDashboard.Models;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace DockerDashboard.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "選擇專案資料夾",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true) return;

        foreach (var folder in dialog.FolderNames)
        {
            if (Projects.Any(p => p.FolderPath.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                continue;

            await AddProjectFromFolderAsync(folder);
            RemoveRecentFolder(folder);
        }

        await SaveSettingsAsync();
        StatusMessage = $"已匯入 {Projects.Count} 個專案";
    }

    [RelayCommand]
    private async Task ImportRecentFolderAsync(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        if (Projects.Any(p => p.FolderPath.Equals(folder, StringComparison.OrdinalIgnoreCase))) return;

        await AddProjectFromFolderAsync(folder);
        RemoveRecentFolder(folder);
        await SaveSettingsAsync();
        StatusMessage = $"已重新匯入 {Path.GetFileName(folder)}";
    }

    [RelayCommand]
    private async Task AllUpAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        IsOperating = true;
        StatusMessage = "正在並行啟動所有服務（快速模式）...";
        LogLines.Clear();
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ▶ 全部啟動（快速模式，不重建 image）");

        var composeFiles = Projects.SelectMany(p => p.ComposeFiles).ToList();
        var errors = new ConcurrentBag<string>();

        var tasks = composeFiles.Select(async compose =>
        {
            try
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] 啟動 {compose.FileName} ...");
                var (exitCode, _) = await _dockerCli.ComposeUpFastWithLogAsync(
                    compose.DirectoryPath, AppendLog);
                if (exitCode != 0)
                    errors.Add(compose.FileName);
                else
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 啟動完成");
            }
            catch (Exception ex)
            {
                errors.Add($"{compose.FileName}: {ex.Message}");
                AppendLog($"[例外] {ex.Message}");
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }

        if (!errors.IsEmpty)
        {
            StatusMessage = $"⚠ {errors.Count} 個服務啟動失敗";
            foreach (var err in errors)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {err}");
        }
        else
        {
            StatusMessage = "✅ 所有服務已啟動";
        }
    }

    [RelayCommand]
    private async Task AllUpBuildAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        IsOperating = true;
        StatusMessage = "正在並行強制重建所有 image 並啟動（不使用 cache）...";
        LogLines.Clear();
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ▶ 全部強制重建（--no-cache，耗時較長）");

        var composeFiles = Projects.SelectMany(p => p.ComposeFiles).ToList();
        var errors = new ConcurrentBag<string>();

        // 循序執行：避免多專案同時 build 佔滿 BuildKit worker 資源
        try
        {
            foreach (var compose in composeFiles)
            {
                try
                {
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] 強制重建+啟動 {compose.FileName} ...");
                    var (exitCode, _) = await _dockerCli.ComposeForceRebuildWithLogAsync(
                        compose.DirectoryPath, AppendLog);
                    if (exitCode != 0)
                        errors.Add(compose.FileName);
                    else
                        AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 強制重建+啟動完成");
                }
                catch (Exception ex)
                {
                    errors.Add($"{compose.FileName}: {ex.Message}");
                    AppendLog($"[例外] {ex.Message}");
                }
            }
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }

        if (!errors.IsEmpty)
        {
            StatusMessage = $"⚠ {errors.Count} 個服務重建失敗";
            foreach (var err in errors)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {err}");
        }
        else
        {
            StatusMessage = "✅ 所有服務已重建並啟動";
        }
    }

    [RelayCommand]
    private async Task AllDownAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        IsOperating = true;
        StatusMessage = "正在並行停止所有服務...";
        LogLines.Clear();
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ■ 全部停止（並行）");

        var composeFiles = Projects.SelectMany(p => p.ComposeFiles).ToList();
        var errors = new ConcurrentBag<string>();

        var tasks = composeFiles.Select(async compose =>
        {
            try
            {
                AppendLog($"[{DateTime.Now:HH:mm:ss}] 停止 {compose.FileName} ...");
                var (exitCode, _) = await _dockerCli.ComposeDownWithLogAsync(
                    compose.DirectoryPath, AppendLog);
                if (exitCode != 0)
                    errors.Add(compose.FileName);
                else
                    AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 停止完成");
            }
            catch (Exception ex)
            {
                errors.Add($"{compose.FileName}: {ex.Message}");
                AppendLog($"[例外] {ex.Message}");
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }

        if (!errors.IsEmpty)
        {
            StatusMessage = $"⚠ {errors.Count} 個服務停止失敗";
            foreach (var err in errors)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {err}");
        }
        else
        {
            StatusMessage = "✅ 所有服務已停止";
        }
    }

    [RelayCommand]
    private async Task StartServiceAsync(DockerService? service)
    {
        if (service == null) return;
        IsOperating = true;
        StatusMessage = $"正在啟動 {service.Name}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ▶ 啟動 {service.Name}");

        try
        {
            var (exitCode, _) = await _dockerCli.ComposeUpFastWithLogAsync(
                service.WorkingDirectory, AppendLog, service.Name);
            if (exitCode != 0)
            {
                StatusMessage = $"⚠ {service.Name} 啟動失敗";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {service.Name} 啟動失敗 (exit code: {exitCode})");
            }
            else
            {
                StatusMessage = $"✅ {service.Name} 已啟動";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {service.Name} 啟動完成");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ {service.Name} 啟動失敗: {ex.Message}";
            AppendLog($"[例外] {ex.Message}");
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task StopServiceAsync(DockerService? service)
    {
        if (service == null) return;
        IsOperating = true;
        StatusMessage = $"正在停止 {service.Name}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ■ 停止 {service.Name}");

        try
        {
            var (exitCode, _) = await _dockerCli.ComposeStopWithLogAsync(
                service.WorkingDirectory, AppendLog, service.Name);
            if (exitCode != 0)
            {
                StatusMessage = $"⚠ {service.Name} 停止失敗";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {service.Name} 停止失敗 (exit code: {exitCode})");
            }
            else
            {
                StatusMessage = $"✅ {service.Name} 已停止";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {service.Name} 停止完成");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ {service.Name} 停止失敗: {ex.Message}";
            AppendLog($"[例外] {ex.Message}");
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task RestartServiceAsync(DockerService? service)
    {
        if (service == null) return;
        IsOperating = true;
        StatusMessage = $"正在重啟 {service.Name}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] 🔄 重啟 {service.Name}（不重建 image）");

        try
        {
            var (exitCode, _) = await _dockerCli.ComposeRestartWithLogAsync(
                service.WorkingDirectory, AppendLog, service.Name);
            if (exitCode != 0)
            {
                StatusMessage = $"⚠ {service.Name} 重啟失敗";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {service.Name} 重啟失敗 (exit code: {exitCode})");
            }
            else
            {
                StatusMessage = $"✅ {service.Name} 已重啟";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {service.Name} 重啟完成");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ {service.Name} 重啟失敗: {ex.Message}";
            AppendLog($"[例外] {ex.Message}");
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task RebuildRestartServiceAsync(DockerService? service)
    {
        if (service == null) return;
        IsOperating = true;
        StatusMessage = $"正在重建並重啟 {service.Name}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] 🔨 重建重啟 {service.Name}（重新 build image）");

        try
        {
            var (exitCode, _) = await _dockerCli.ComposeRebuildRestartWithLogAsync(
                service.WorkingDirectory, AppendLog, service.Name);
            if (exitCode != 0)
            {
                StatusMessage = $"⚠ {service.Name} 重建重啟失敗";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {service.Name} 重建重啟失敗 (exit code: {exitCode})");
            }
            else
            {
                StatusMessage = $"✅ {service.Name} 已重建並重啟";
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {service.Name} 重建重啟完成");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"⚠ {service.Name} 重建重啟失敗: {ex.Message}";
            AppendLog($"[例外] {ex.Message}");
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task ComposeUpAsync(ComposeFile? compose)
    {
        if (compose == null) return;
        IsOperating = true;
        StatusMessage = $"正在啟動 {compose.FileName}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ▶ 啟動 {compose.FileName}");

        try
        {
            var (exitCode, _) = await _dockerCli.ComposeUpFastWithLogAsync(compose.DirectoryPath, AppendLog);
            if (exitCode == 0)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 啟動完成");
            else
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 啟動失敗");
            StatusMessage = exitCode == 0 ? $"✅ {compose.FileName} 已啟動" : $"⚠ {compose.FileName} 啟動失敗";
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 例外: {ex.Message}");
            StatusMessage = $"⚠ {compose.FileName} 啟動失敗";
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task ComposeDownAsync(ComposeFile? compose)
    {
        if (compose == null) return;
        IsOperating = true;
        StatusMessage = $"正在停止 {compose.FileName}...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ■ 停止 {compose.FileName}");

        try
        {
            var (exitCode, _) = await _dockerCli.ComposeDownWithLogAsync(compose.DirectoryPath, AppendLog);
            if (exitCode == 0)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 停止完成");
            else
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 停止失敗");
            StatusMessage = exitCode == 0 ? $"✅ {compose.FileName} 已停止" : $"⚠ {compose.FileName} 停止失敗";
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 例外: {ex.Message}");
            StatusMessage = $"⚠ {compose.FileName} 停止失敗";
        }
        finally
        {
            await _monitor.ForceRefreshAsync();
            IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task ComposePullAsync(ComposeFile? compose)
    {
        if (compose == null) return;
        IsOperating = true;
        StatusMessage = $"正在拉取 {compose.FileName} 映像...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ⬇ 拉取映像 {compose.FileName}");

        try
        {
            var (exitCode, _) = await _dockerCli.ComposePullWithLogAsync(compose.DirectoryPath, AppendLog);
            if (exitCode == 0)
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 映像拉取完成");
            else
                AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 映像拉取失敗");
            StatusMessage = exitCode == 0 ? $"✅ {compose.FileName} 映像已更新" : $"⚠ {compose.FileName} 映像拉取失敗";
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] ❌ {compose.FileName} 例外: {ex.Message}");
            StatusMessage = $"⚠ {compose.FileName} 映像拉取失敗";
        }
        finally
        {
            IsOperating = false;
        }
    }

    [RelayCommand]
    private async Task PullAllImagesAsync()
    {
        if (Projects.Count == 0)
        {
            StatusMessage = "⚠ 請先匯入專案資料夾";
            return;
        }

        IsOperating = true;
        StatusMessage = "正在拉取所有映像...";
        AppendLog($"[{DateTime.Now:HH:mm:ss}] ⬇ 拉取所有映像（並行）");

        var composeFiles = Projects.SelectMany(p => p.ComposeFiles).ToList();
        var errors = new ConcurrentBag<string>();

        var tasks = composeFiles.Select(async compose =>
        {
            try
            {
                var (exitCode, _) = await _dockerCli.ComposePullWithLogAsync(compose.DirectoryPath, AppendLog);
                if (exitCode != 0) errors.Add(compose.FileName);
                else AppendLog($"[{DateTime.Now:HH:mm:ss}] ✅ {compose.FileName} 映像拉取完成");
            }
            catch (Exception ex)
            {
                errors.Add($"{compose.FileName}: {ex.Message}");
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            IsOperating = false;
        }
        StatusMessage = errors.IsEmpty ? "✅ 所有映像已更新" : $"⚠ {errors.Count} 個映像拉取失敗";
    }

    [RelayCommand]
    private async Task RemoveProjectAsync(DockerProject? project)
    {
        if (project == null) return;

        if (!RecentlyRemovedFolders.Contains(project.FolderPath))
        {
            RecentlyRemovedFolders.Add(project.FolderPath);
            if (RecentlyRemovedFolders.Count > 10)
                RecentlyRemovedFolders.RemoveAt(0);
        }

        Projects.Remove(project);
        await SaveSettingsAsync();
        UpdateCounts();
        StatusMessage = $"已移除 {project.Name}";
    }

    [RelayCommand]
    private async Task RescanProjectsAsync()
    {
        var folders = Projects.Select(p => p.FolderPath).ToList();
        Projects.Clear();

        var results = await Task.WhenAll(
            folders.Select(async folder =>
            {
                var project = new DockerProject
                {
                    Name = Path.GetFileName(folder),
                    FolderPath = folder
                };
                if (Directory.Exists(folder))
                {
                    var composeFiles = await _scanner.ScanFolderAsync(folder);
                    foreach (var cf in composeFiles)
                        project.ComposeFiles.Add(cf);
                }
                return project;
            }));

        foreach (var project in results)
        {
            Projects.Add(project);
            if (Directory.Exists(project.FolderPath) && project.ComposeFiles.Count == 0)
                StatusMessage = $"⚠ {project.Name} 中未偵測到服務（docker compose config 可能失敗）";
        }

        _watchService.ClearAll();
        var settings = await _settingsService.LoadAsync();
        RestoreWatchStateFromSettings(settings);

        await _monitor.ForceRefreshAsync();
        StatusMessage = "重新掃描完成";
    }

    [RelayCommand]
    private async Task ToggleWatchServiceAsync(DockerService? service)
    {
        if (service == null) return;

        service.IsWatching = !service.IsWatching;

        if (service.IsWatching)
            _watchService.AddWatch(service.WorkingDirectory, service.Name);
        else
            _watchService.RemoveWatch(service.WorkingDirectory, service.Name);

        await SaveSettingsAsync();
    }

    [RelayCommand]
    private void OpenInBrowser(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void OpenRepairWindow()
    {
        var window = new Views.DockerRepairWindow(_dockerCli)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var settings = await _settingsService.LoadAsync();
        var settingsWindow = new Views.SettingsWindow(settings);
        settingsWindow.Owner = Application.Current.MainWindow;

        if (settingsWindow.ShowDialog() == true)
        {
            await _settingsService.SaveAsync(settings);
            ApplyDockerModeSettings(settings);
            ApplyWatchSettings(settings);
            await _monitor.StopAsync();
            _monitor.Start(TimeSpan.FromSeconds(settings.PollIntervalSeconds));
            StatusMessage = "設定已儲存並套用";
        }
    }
}
