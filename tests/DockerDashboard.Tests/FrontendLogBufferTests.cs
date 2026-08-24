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
}
