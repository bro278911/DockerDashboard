using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

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

    public ObservableCollection<ComposeFile> ComposeFiles { get; } = [];
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
