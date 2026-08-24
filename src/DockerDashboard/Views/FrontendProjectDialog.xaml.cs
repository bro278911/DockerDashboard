using System.Windows;
using DockerDashboard.Models;

namespace DockerDashboard.Views;

public partial class FrontendProjectDialog : Window
{
    private readonly FrontendProject _project;

    public FrontendProjectDialog(FrontendProject project)
    {
        InitializeComponent();
        _project = project;
        DataContext = project;
        GroupCombo.SelectedIndex = (int)project.Group;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_project.Name))
        {
            System.Windows.MessageBox.Show("顯示名稱不可空白", "加入前端專案",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _project.Group = (FrontendGroup)GroupCombo.SelectedIndex;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
