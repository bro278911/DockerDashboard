using System.Windows;
using DockerDashboard.Models;

namespace DockerDashboard.Views;

public partial class FrontendProjectDialog : Window
{
    private readonly FrontendProject _project;
    private readonly string _title;

    public FrontendProjectDialog(FrontendProject project, bool isEdit = false)
    {
        InitializeComponent();
        _project = project;
        _title = isEdit ? "編輯前端專案" : "加入前端專案";
        Title = _title;
        ConfirmButton.Content = isEdit ? "儲存" : "加入";
        DataContext = project;
        var initialGroup = isEdit
            ? project.Group
            : FrontendProject.GuessGroupFromPath(project.FolderPath);
        GroupCombo.SelectedIndex = initialGroup is { } group ? (int)group : -1;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_project.Name))
        {
            System.Windows.MessageBox.Show("顯示名稱不可空白", _title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (GroupCombo.SelectedIndex < 0)
        {
            System.Windows.MessageBox.Show("請選擇群組", _title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _project.Group = (FrontendGroup)GroupCombo.SelectedIndex;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
