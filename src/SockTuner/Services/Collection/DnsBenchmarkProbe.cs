using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SockTuner.Models;

namespace SockTuner.Services.Collection;

/// <summary>
/// Collection layer: measures how quickly each candidate resolver answers.
/// </summary>
/// <remarks>
/// <para>
/// Queries are built and sent over UDP rather than going through <c>Dns.GetHostAddresses</c>,
/// because the .NET resolver uses whatever Windows is configured to use — it cannot be pointed at a
/// resolver that is not already in use, which is exactly what a benchmark has to do.
/// </para>
/// <para>
/// This measures name-lookup time, which is not the latency of an established session. A faster
/// resolver shortens the pause before a connection starts; it does not change the ping inside a
/// game that is already connected.
/// </para>
/// </remarks>
public sealed class DnsBenchmarkProbe
{
    public const int DnsPort = 53;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Resolvers offered by default. The list is deliberately short and well known.</summary>
    public static IReadOnlyList<DnsResolverCandidate> WellKnown { get; } =
    [
        new("Cloudflare", "1.1.1.1"),
        new("Cloudflare secondary", "1.0.0.1"),
        new("Google", "8.8.8.8"),
        new("Google secondary", "8.8.4.4"),
        new("Quad9", "9.9.9.9"),
        new("OpenDNS", "208.67.222.222")
    ];

    /// <summary>Names chosen to be widely resolvable and unlikely to sit in one provider's cache only.</summary>
    public static IReadOnlyList<string> DefaultHostnames { get; } =
        ["example.com", "wikipedia.org", "github.com", "cloudflare.com"];

    private readonly Func<IPAddress, string, TimeSpan, CancellationToken, Task<double?>> _query;

    public DnsBenchmarkProbe() : this(QueryAsync) { }

    internal DnsBenchmarkProbe(Func<IPAddress, string, TimeSpan, CancellationToken, Task<double?>> query) =>
        _query = query;

    public async Task<DnsBenchmarkReport> RunAsync(
        IReadOnlyList<DnsResolverCandidate> resolvers,
        IReadOnlyList<string> hostnames,
        int roundsPerHost,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(resolvers.Count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(hostnames.Count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(roundsPerHost, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(roundsPerHost, 10);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var results = new List<DnsResolverResult>();
        foreach (var resolver in resolvers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await MeasureAsync(resolver, hostnames, roundsPerHost, timeout, cancellationToken));
        }

        var current = results.FirstOrDefault(result => result.Resolver.InUse);
        var fastest = results
            .Where(result => result.Usable)
            .OrderBy(result => result.MedianMs)
            .FirstOrDefault();

        return new DnsBenchmarkReport(results, fastest, current, Verdict(fastest, current));
    }

    private async Task<DnsResolverResult> MeasureAsync(
        DnsResolverCandidate resolver,
        IReadOnlyList<string> hostnames,
        int roundsPerHost,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(resolver.Address, out var address))
        {
            return new DnsResolverResult(resolver, 0, 0, null, null, null, null, "Not a valid IP address.");
        }

        var samples = new List<double>();
        var queries = 0;
        string? error = null;

        foreach (var host in hostnames)
        {
            for (var round = 0; round < roundsPerHost; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                queries++;
                try
                {
                    if (await _query(address, host, timeout, cancellationToken) is { } elapsed)
                    {
                        samples.Add(elapsed);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    error ??= exception.Message;
                }
            }
        }

        if (samples.Count == 0)
        {
            return new DnsResolverResult(resolver, queries, 0, null, null, null, null, error);
        }

        var ordered = samples.OrderBy(value => value).ToArray();
        return new DnsResolverResult(
            resolver,
            queries,
            samples.Count,
            Median(ordered),
            samples.Average(),
            ordered[0],
            ordered[^1],
            error);
    }

    internal static double Median(double[] ordered) => ordered.Length % 2 == 1
        ? ordered[ordered.Length / 2]
        : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;

    private static string Verdict(DnsResolverResult? fastest, DnsResolverResult? current)
    {
        if (fastest is null)
        {
            return "No resolver answered reliably enough to rank. That usually means outbound port 53 is blocked or intercepted.";
        }

        if (current is null)
        {
            return $"{fastest.Resolver.Name} was fastest at {fastest.MedianMs:0.0} ms. The resolver currently in use was not among those measured, so there is nothing to compare it against.";
        }

        if (ReferenceEquals(fastest, current))
        {
            return $"The resolver already in use was the fastest measured, at {current.MedianMs:0.0} ms. Nothing to change.";
        }

        if (current.MedianMs is not { } currentMs)
        {
            return $"{fastest.Resolver.Name} answered in {fastest.MedianMs:0.0} ms; the resolver in use did not answer reliably enough to be ranked.";
        }

        var gain = currentMs - fastest.MedianMs!.Value;
        return gain < 5
            ? $"{fastest.Resolver.Name} was fastest at {fastest.MedianMs:0.0} ms, but only {gain:0.0} ms ahead of the one in use. That is inside normal run-to-run variation and is not a reason to change."
            : $"{fastest.Resolver.Name} answered {gain:0.0} ms faster than the resolver in use ({fastest.MedianMs:0.0} ms against {currentMs:0.0} ms). This shortens the pause before a connection starts; it does not change the latency of a session already connected.";
    }

    /// <summary>
    /// Sends one A query and returns the round trip in milliseconds, or null when nothing valid came
    /// back before the timeout. Only replies whose transaction id matches are counted.
    /// </summary>
    private static async Task<double?> QueryAsync(
        IPAddress server, string hostname, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        var transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = BuildQuery(transactionId, hostname);
        var endpoint = new IPEndPoint(server, DnsPort);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await socket.SendToAsync(query, SocketFlags.None, endpoint, deadline.Token);
            var buffer = new byte[512];
            var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, endpoint, deadline.Token);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            if (received.ReceivedBytes < 4) return null;
            if (BinaryPrimitives.ReadUInt16BigEndian(buffer) != transactionId) return null;

            // Low four bits of the second flags byte are the response code; anything other than 0
            // means the resolver answered but did not resolve, which is not a usable sample.
            return (buffer[3] & 0x0F) == 0 ? elapsed : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;   // timed out
        }
        catch (SocketException)
        {
            return null;
        }
    }

    internal static byte[] BuildQuery(ushort transactionId, string hostname)
    {
        var labels = hostname.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var length = 12 + labels.Sum(label => label.Length + 1) + 1 + 4;
        var packet = new byte[length];

        BinaryPrimitives.WriteUInt16BigEndian(packet, transactionId);
        packet[2] = 0x01;                                    // standard query, recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 1);   // one question

        var offset = 12;
        foreach (var label in labels)
        {
            packet[offset++] = (byte)label.Length;
            offset += Encoding.ASCII.GetBytes(label, 0, label.Length, packet, offset);
        }

        packet[offset++] = 0;                                // root label
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset), 1);       // QTYPE A
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset + 2), 1);   // QCLASS IN
        return packet;
    }
}
