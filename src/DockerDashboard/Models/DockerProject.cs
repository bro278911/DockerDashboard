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

    public ObservableCollection<DockerService> Services { get; } = [];
}
