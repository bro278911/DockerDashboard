using System.Linq;
using System.Windows;

namespace DockerDashboard.Views;

public partial class Wsl2GuideWindow : Window
{
    public Wsl2GuideWindow()
    {
        InitializeComponent();
    }

    private void CopyBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn &&
            btn.Parent is System.Windows.Controls.StackPanel sp)
        {
            var box = sp.Children.OfType<System.Windows.Controls.TextBox>().FirstOrDefault();
            if (box != null) Copy(box.Text);
        }
    }

    private static void Copy(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
