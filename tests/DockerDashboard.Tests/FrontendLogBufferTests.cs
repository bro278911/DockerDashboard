using DockerDashboard.ViewModels;

namespace DockerDashboard.Tests;

public class FrontendLogBufferTests
{
    [Fact]
    public void AppendLine_超過5000行_保留後4500行()
    {
        var buffer = new FrontendLogBuffer();
        for (var i = 0; i < 5001; i++)
            buffer.AppendLine($"line-{i}");

        Assert.Equal(4501, buffer.Lines.Count);
        Assert.Equal("line-500", buffer.Lines[0]);
        Assert.Equal("line-5000", buffer.Lines[^1]);
    }

    [Theory]
    [InlineData("ERROR: fail", "error", true)]
    [InlineData("ok line", "error", false)]
    [InlineData("anything", "", true)]
    [InlineData("anything", "   ", true)]
    public void MatchesFilter_不分大小寫關鍵字(string line, string filter, bool expected)
    {
        Assert.Equal(expected, FrontendLogBuffer.MatchesFilter(line, filter));
    }

    // 回歸測試：測試環境（xunit host）沒有 WPF Application，Application.Current 為 null。
    // 舊實作在此情境同步寫入 Lines，正式環境若在 App 關閉後（Application.Current 變 null）
    // 仍有背景執行緒呼叫 Append，會在該執行緒直接改 ObservableCollection，行為與正式環境
    // 的 MainViewModel.AppendLog（null 時安靜丟棄）不一致、且有風險。應比照安靜丟棄。
    [Fact]
    public void Append_無WPFApplication時不拋例外且不同步寫入()
    {
        var buffer = new FrontendLogBuffer();

        var ex = Record.Exception(() => buffer.Append("test line"));

        Assert.Null(ex);
        Assert.Empty(buffer.Lines);
    }

    // 回歸測試：沒有 dispatcher 時若只是「不排程」，_flushScheduled 會卡在已排程狀態、
    // 待處理佇列無限成長（宣稱的安靜丟棄其實是安靜累積）。應真的丟棄。
    [Fact]
    public void Append_無WPFApplication時待處理佇列不會累積()
    {
        var buffer = new FrontendLogBuffer();

        for (var i = 0; i < 1000; i++)
            buffer.Append($"line-{i}");

        Assert.Empty(buffer.Lines);
        Assert.Equal(0, buffer.PendingCount);
    }
}
