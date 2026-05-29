using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DockerDashboard.Views;

public partial class BranchSelectorWindow : Window, INotifyPropertyChanged
{
    private readonly List<BranchItem> _allBranches;

    public string ProjectName { get; }
    public string? SelectedBranch { get; private set; }
    public bool HasSelection => BranchListBox?.SelectedItem != null;

    public event PropertyChangedEventHandler? PropertyChanged;

    public BranchSelectorWindow(
        string projectName,
        string currentBranch,
        List<string> localBranches,
        List<string> remoteBranches)
    {
        ProjectName = projectName;
        DataContext = this;

        _allBranches = [];

        foreach (var branch in localBranches)
        {
            _allBranches.Add(new BranchItem
            {
                Name = branch,
                IsLocal = true,
                IsCurrent = branch == currentBranch
            });
        }

        foreach (var branch in remoteBranches)
        {
            var localName = branch.Contains('/')
                ? branch[(branch.IndexOf('/') + 1)..]
                : branch;

            if (localBranches.Contains(localName)) continue;

            _allBranches.Add(new BranchItem
            {
                Name = branch,
                IsLocal = false,
                IsCurrent = false
            });
        }

        InitializeComponent();
        BranchListBox.ItemsSource = _allBranches;
        BranchListBox.SelectionChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelection)));

        var currentItem = _allBranches.FirstOrDefault(b => b.IsCurrent);
        if (currentItem != null)
            BranchListBox.SelectedItem = currentItem;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = SearchBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(filter))
        {
            BranchListBox.ItemsSource = _allBranches;
        }
        else
        {
            BranchListBox.ItemsSource = _allBranches
                .Where(b => b.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void BranchListBox_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BranchListBox.SelectedItem is BranchItem item && !item.IsCurrent)
        {
            SelectedBranch = item.Name;
            DialogResult = true;
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (BranchListBox.SelectedItem is BranchItem item)
        {
            if (item.IsCurrent)
            {
                System.Windows.MessageBox.Show("已經在此分支上。", "提示", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }
            SelectedBranch = item.Name;
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

public class BranchItem
{
    public string Name { get; set; } = string.Empty;
    public bool IsLocal { get; set; }
    public bool IsCurrent { get; set; }

    public string Icon => IsLocal ? "SourceBranch" : "CloudOutline";
    public string Color => IsCurrent ? "#4CAF50" : (IsLocal ? "#2196F3" : "#9E9E9E");
    public string FontWeight => IsCurrent ? "Bold" : "Normal";
    public Visibility IsCurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
}
