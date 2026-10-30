using SockTuner.Models;

namespace SockTuner.Tests;

public sealed class ProcessConnectionsTests
{
    [Fact]
    public void Summarize_GroupsByProcessAndCountsEstablishedSeparately()
    {
        ProcessConnectionInfo Connection(int pid, string name, TcpConnectionState state) => new(
            "IPv4", "192.168.1.10", 51000 + pid, "198.51.100.10", 443, state, pid, name);

        var connections = new[]
        {
            Connection(100, "game.exe", TcpConnectionState.Established),
            Connection(100, "game.exe", TcpConnectionState.Established),
            Connection(100, "game.exe", TcpConnectionState.TimeWait),
            Connection(200, "browser.exe", TcpConnectionState.Established)
        };

        var summary = ProcessConnectionSummary.Summarize(connections);

        Assert.Equal(
            [(100, "game.exe", 3, 2), (200, "browser.exe", 1, 1)],
            summary.Select(item => (item.ProcessId, item.ProcessName, item.ConnectionCount, item.EstablishedCount)));
    }

    [Fact]
    public void Summarize_OrdersBusiestProcessFirst()
    {
        ProcessConnectionInfo Connection(int pid, string name) => new(
            "IPv4", "192.168.1.10", 51000 + pid, "198.51.100.10", 443, TcpConnectionState.Established, pid, name);

        var connections = new[] { Connection(1, "quiet.exe"), Connection(2, "busy.exe"), Connection(2, "busy.exe") };

        var summary = ProcessConnectionSummary.Summarize(connections);

        Assert.Equal(["busy.exe", "quiet.exe"], summary.Select(item => item.ProcessName));
    }
}
