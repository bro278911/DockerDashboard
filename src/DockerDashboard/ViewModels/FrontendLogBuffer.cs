using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DockerDashboard.ViewModels;

/// <summary>單一群組的前端 log 緩衝：5000 行上限、關鍵字過濾、跨執行緒批次寫入</summary>
public sealed partial class FrontendLogBuffer : ObservableObject
{
    private readonly ConcurrentQueue<string> _pending = new();
    private int _flushScheduled;

    private ICollectionView? _view;

    public BulkObservableCollection<string> Lines { get; } = [];

    // 延遲建立：XAML 綁定時（UI 執行緒）才產生 view，單元測試不必起 WPF Application
    public ICollectionView View
    {
        get
        {
            if (_view != null) return _view;
            _view = CollectionViewSource.GetDefaultView(Lines);
            _view.Filter = item => item is string line && MatchesFilter(line, Filter);
            return _view;
        }
    }

    [ObservableProperty]
    private string _filter = string.Empty;

    partial void OnFilterChanged(string value) => _view?.Refresh();

    internal static bool MatchesFilter(string line, string filter)
        => string.IsNullOrWhiteSpace(filter)
           || line.Contains(filter, StringComparison.OrdinalIgnoreCase);

    public void Append(string message)
    {
        _pending.Enqueue(message);
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 1) return;
        ScheduleFlush();
    }

    internal int PendingCount => _pending.Count;

    // Application.Current 為 null（測試環境、或 App 已關閉）時安靜丟棄，比照 MainViewModel.AppendLog；
    // 不同步寫入，避免背景執行緒直接改 Lines（ObservableCollection 非執行緒安全）。
    // 丟棄必須真的清空佇列並歸零旗標，否則旗標會卡在「已排程」，之後每次 Append 都只入隊不排程，
    // 待處理佇列無限成長
    private void ScheduleFlush()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            while (_pending.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _flushScheduled, 0);
            return;
        }

        dispatcher.InvokeAsync(Flush);
    }

    private void Flush()
    {
        try
        {
            while (_pending.TryDequeue(out var line))
                AppendLine(line);
        }
        finally
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            if (!_pending.IsEmpty && Interlocked.Exchange(ref _flushScheduled, 1) == 0)
                ScheduleFlush();
        }
    }

    internal void AppendLine(string message)
    {
        Lines.Add(message);
        if (Lines.Count <= 5000) return;
        // 裁剪走 ReplaceAll：只發一次 Reset 通知。逐筆 re-add 會在高輸出量的 dev server 下
        // 每次裁剪產生約 4500 次集合通知與過濾判斷，造成週期性卡頓
        Lines.ReplaceAll(Lines.Skip(500).ToArray());
    }

    // 一併清掉排隊中的行，否則按下清除後、下一次 Flush 會立刻把當時已排隊的內容補回來，
    // 看起來像沒清乾淨
    public void Clear()
    {
        while (_pending.TryDequeue(out _)) { }
        Lines.Clear();
    }
}
