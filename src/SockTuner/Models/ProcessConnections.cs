namespace SockTuner.Models;

/// <summary>Windows <c>MIB_TCP_STATE</c>.</summary>
public enum TcpConnectionState
{
    Closed = 1,
    Listen = 2,
    SynSent = 3,
    SynReceived = 4,
    Established = 5,
    FinWait1 = 6,
    FinWait2 = 7,
    CloseWait = 8,
    Closing = 9,
    LastAck = 10,
    TimeWait = 11,
    DeleteTcb = 12
}

/// <summary>One active TCP connection and the process that owns it.</summary>
public sealed record ProcessConnectionInfo(
    string AddressFamily,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    TcpConnectionState State,
    int ProcessId,
    string ProcessName);

public sealed record ProcessConnectionInventoryResult(IReadOnlyList<ProcessConnectionInfo> Connections, string? Error);

/// <summary>
/// One process's share of the active TCP connections — "which process is this link actually busy
/// with" without packet capture, reviving the capability GameNetAnalyzer proved out (see project
/// roadmap) on SockTuner's own non-capture path instead of importing its reports.
/// </summary>
public sealed record ProcessConnectionSummary(int ProcessId, string ProcessName, int ConnectionCount, int EstablishedCount)
{
    public static IReadOnlyList<ProcessConnectionSummary> Summarize(IReadOnlyList<ProcessConnectionInfo> connections) =>
        connections
            .GroupBy(connection => (connection.ProcessId, connection.ProcessName))
            .Select(group => new ProcessConnectionSummary(
                group.Key.ProcessId,
                group.Key.ProcessName,
                group.Count(),
                group.Count(connection => connection.State == TcpConnectionState.Established)))
            .OrderByDescending(summary => summary.ConnectionCount)
            .ThenBy(summary => summary.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
