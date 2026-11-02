using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DockerDashboard.Services;

namespace DockerDashboard.Models;

public partial class DockerProject : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private bool _isGitRepo;

    [ObservableProperty]
    private string _currentBranch = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    // 此專案所有 .NET 服務共用的 ASPNETCORE_ENVIRONMENT
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnvironmentChoices), nameof(IsStageEnvironment), nameof(IsProductionEnvironment))]
    private string _selectedEnvironment = ProjectEnvironment.Development;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnvironmentChoices))]
    private IReadOnlyList<string> _availableEnvironments = [ProjectEnvironment.Development];

    public bool IsStageEnvironment => SelectedEnvironment == ProjectEnvironment.Stage;
    public bool IsProductionEnvironment => SelectedEnvironment == ProjectEnvironment.Production;

    // 右鍵「環境」子選單的項目
    public IReadOnlyList<EnvironmentChoice> EnvironmentChoices =>
        [.. AvailableEnvironments.Select(e => new EnvironmentChoice(this, e, e == SelectedEnvironment))];

    public ObservableCollection<ComposeFile> ComposeFiles { get; } = [];
}

public sealed record EnvironmentChoice(DockerProject Project, string Name, bool IsCurrent)
{
    public string DisplayName => IsCurrent ? $"✔ {Name}" : Name;
}

public partial class ComposeFile : ObservableObject
{
    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _directoryPath = string.Empty;

    // compose project name（config 的 name 欄位，未明訂時為資料夾名衍生預設值）
    public string ProjectName { get; set; } = string.Empty;

    public ObservableCollection<DockerService> Services { get; } = [];
}
