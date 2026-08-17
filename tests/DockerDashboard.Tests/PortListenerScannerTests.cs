using DockerDashboard.Services;

namespace DockerDashboard.Tests;

public class PortListenerScannerTests
{
    [Fact]
    public void Parse_回傳指定Port的IPv4監聽器()
    {
        const string output = """
            Active Connections

              Proto  Local Address          Foreign Address        State           PID
              TCP    0.0.0.0:80             0.0.0.0:0              LISTENING       27400
            """;

        var listener = Assert.Single(PortListenerScanner.Parse(output, 80));

        Assert.Equal(27400, listener.Pid);
        Assert.Equal("0.0.0.0:80", listener.Address);
    }

    [Fact]
    public void Parse_回傳指定Port的IPv6監聽器()
    {
        const string output = "  TCP    [::1]:80               [::]:0                 LISTENING       29588";

        var listener = Assert.Single(PortListenerScanner.Parse(output, 80));

        Assert.Equal(29588, listener.Pid);
        Assert.Equal("[::1]:80", listener.Address);
    }

    [Fact]
    public void Parse_不會比對到其他Port的尾碼()
    {
        const string output = "  TCP    0.0.0.0:8080           0.0.0.0:0              LISTENING       1234";

        Assert.Empty(PortListenerScanner.Parse(output, 80));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Parse_略過系統保留Pid(int pid)
    {
        var output = $"  TCP    0.0.0.0:80             0.0.0.0:0              LISTENING       {pid}";

        Assert.Empty(PortListenerScanner.Parse(output, 80));
    }

    [Fact]
    public void Parse_相同Pid只保留第一個位址()
    {
        const string output = """
              TCP    0.0.0.0:80             0.0.0.0:0              LISTENING       27400
              TCP    [::]:80                [::]:0                 LISTENING       27400
            """;

        var listener = Assert.Single(PortListenerScanner.Parse(output, 80));

        Assert.Equal("0.0.0.0:80", listener.Address);
    }

    [Fact]
    public void Parse_略過非監聽狀態()
    {
        const string output = """
              TCP    127.0.0.1:50503        127.0.0.1:80           TIME_WAIT       0
              TCP    [::1]:80               [::1]:52160            ESTABLISHED     29588
              TCP    127.0.0.1:80           127.0.0.1:52161        CLOSE_WAIT      29589
            """;

        Assert.Empty(PortListenerScanner.Parse(output, 80));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not netstat output")]
    [InlineData("UDP 0.0.0.0:80 *:* 1234")]
    public void Parse_空白或無效輸入回傳空集合(string output)
    {
        Assert.Empty(PortListenerScanner.Parse(output, 80));
    }
}
