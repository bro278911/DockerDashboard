using System.Windows;
using System.Windows.Controls;
using DockerDashboard.Models;
using DockerDashboard.ViewModels;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace DockerDashboard;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Forms.NotifyIcon? _notifyIcon;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
        StateChanged += OnStateChanged;
        InitializeTrayIcon();
        _viewModel.SetNotifyIcon(_notifyIcon);
    }

    private void InitializeTrayIcon()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Docker Dashboard",
            Visible = false
        };

        var iconPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Assets", "docker.ico");
        if (System.IO.File.Exists(iconPath))
            _notifyIcon.Icon = new Drawing.Icon(iconPath);
        else
            _notifyIcon.Icon = Drawing.SystemIcons.Application;

        var contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.Add("顯示主視窗", null, (_, _) => RestoreFromTray());
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add("結束", null, (_, _) =>
        {
            _notifyIcon.Visible = false;
            System.Windows.Application.Current.Shutdown();
        });
        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void OnStateChanged(object? sender, System.EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            if (_notifyIcon != null)
                _notifyIcon.Visible = true;
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_notifyIcon != null)
            _notifyIcon.Visible = false;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is DockerService service)
            _viewModel.SelectedService = service;
        else
        {
            _viewModel.SelectedService = null;
            if (e.NewValue is ComposeFile compose)
                _viewModel.SelectedComposeFile = compose;
        }
    }

    private void OpenPort_Click(object sender, RoutedEventArgs e)
    {
        var ports = _viewModel.SelectedService?.Ports;
        var links = _viewModel.ParsePortLinks(ports);
        foreach (var link in links)
            _viewModel.OpenInBrowserCommand.Execute(link);
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        _viewModel.Dispose();
    }
}