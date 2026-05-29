using System.Windows;

namespace DockerDashboard.Views;

public partial class Wsl2GuideWindow : Window
{
    public Wsl2GuideWindow()
    {
        InitializeComponent();
    }

    private void CopyStep1_Click(object sender, RoutedEventArgs e)  => Copy(Step1Box.Text);
    private void CopyStep2_Click(object sender, RoutedEventArgs e)  => Copy(Step2Box.Text);
    private void CopyStep3_Click(object sender, RoutedEventArgs e)  => Copy(Step3Box.Text);
    private void CopyStep4a_Click(object sender, RoutedEventArgs e) => Copy(Step4aBox.Text);
    private void CopyStep4b_Click(object sender, RoutedEventArgs e) => Copy(Step4bBox.Text);
    private void CopyStep5_Click(object sender, RoutedEventArgs e)  => Copy(Step5Box.Text);
    private void CopyFix1_Click(object sender, RoutedEventArgs e)   => Copy(Fix1Box.Text);

    private static void Copy(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
