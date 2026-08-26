using System.Linq;
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
    private bool _isExiting;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ConstrainToWorkArea();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
        Closing += OnClosing;
        // 系統登出/關機時不可取消關閉，否則會擋住 Windows 關機
        System.Windows.Application.Current.SessionEnding += (_, _) => _isExiting = true;
        InitializeTrayIcon();
        _viewModel.SetNotifyIcon(_notifyIcon);
    }

    // 小螢幕（或縮小比例）下，固定的 1200x750 初始尺寸可能超出可用工作區，
    // 導致標題列的最小化/關閉按鈕被推出畫面外。啟動時依「主螢幕」可用工作區收斂初始尺寸
    // （SystemParameters.WorkArea 僅反映主螢幕，多螢幕情境為近似值）。
    // 只調整初始 Width/Height，不設 MaxWidth/MaxHeight，避免之後最大化或移到更大螢幕時被永久鎖死。
    private void ConstrainToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        const double margin = 20;
        Width = Math.Min(Width, workArea.Width - margin);
        Height = Math.Min(Height, workArea.Height - margin);
    }

    private void InitializeTrayIcon()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Docker Dashboard",
            Visible = false
        };

        var iconPath = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Assets", "pooh.ico");
        if (System.IO.File.Exists(iconPath))
            _notifyIcon.Icon = new Drawing.Icon(iconPath);
        else
            _notifyIcon.Icon = Drawing.SystemIcons.Application;

        var contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.Add("顯示主視窗", null, (_, _) => RestoreFromTray());
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add("結束", null, (_, _) =>
        {
            _isExiting = true;
            _notifyIcon.Visible = false;
            System.Windows.Application.Current.Shutdown();
        });
        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExiting)
            return;

        e.Cancel = true;
        Hide();
        if (_notifyIcon != null)
            _notifyIcon.Visible = true;
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
        {
            _viewModel.SelectedService = service;
            _viewModel.SelectedProject = _viewModel.Projects
                .FirstOrDefault(p => p.ComposeFiles.Any(c => c.Services.Contains(service)));
        }
        else if (e.NewValue is ComposeFile compose)
        {
            _viewModel.SelectedService = null;
            _viewModel.SelectedComposeFile = compose;
            _viewModel.SelectedProject = _viewModel.Projects
                .FirstOrDefault(p => p.ComposeFiles.Contains(compose));
        }
        else if (e.NewValue is DockerProject project)
        {
            _viewModel.SelectedService = null;
            _viewModel.SelectedProject = project;
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
            // WinForms NotifyIcon.Dispose() 設外部 Icon 時 ownIcon=false，不會 dispose Icon，
            // 須手動釋放 HICON，避免 GDI handle 洩漏
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        _viewModel.Dispose();
    }
}