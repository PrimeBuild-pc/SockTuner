using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using SockTuner.Models;

namespace SockTuner.Services.Collection;

/// <summary>
/// Collection layer: moves bytes against a user-chosen endpoint for a bounded window and reports
/// the rate. It never starts on its own, never picks an endpoint on the user's behalf, and draws
/// no conclusion about whether the result is good.
/// </summary>
public sealed class ThroughputProbe
{
    public const int DefaultStreams = 4;
    public const int MaximumStreams = 8;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(2);

    // One transfer on one stream: moves bytes until the token is cancelled and reports how many.
    private readonly Func<string, TransferDirection, CancellationToken, Task<long>> _transfer;

    public ThroughputProbe() : this(TransferAsync) { }

    internal ThroughputProbe(Func<string, TransferDirection, CancellationToken, Task<long>> transfer) =>
        _transfer = transfer;

    /// <summary>
    /// Runs until <paramref name="duration"/> elapses or <paramref name="cancellationToken"/> is
    /// cancelled. A cancelled run still returns its measured rate with
    /// <see cref="ThroughputResult.Completed"/> false — a stopped transfer has moved real bytes over
    /// real time, and the loaded-latency probe depends on being able to stop the load early.
    /// </summary>
    public async Task<ThroughputResult> RunAsync(
        string endpoint,
        TransferDirection direction,
        int streams,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentOutOfRangeException.ThrowIfLessThan(streams, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(streams, MaximumStreams);
        if (duration <= TimeSpan.Zero || duration > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(duration);
        var startedAt = DateTimeOffset.Now;
        var transfers = Enumerable.Range(0, streams)
            .Select(_ => RunStreamAsync(endpoint, direction, stop.Token))
            .ToArray();

        var results = await Task.WhenAll(transfers);
        var elapsed = DateTimeOffset.Now - startedAt;
        var failure = results.Select(result => result.Failure).FirstOrDefault(item => item is not null);
        var bytes = results.Sum(result => result.Bytes);

        // Only a failure that moved nothing at all is reported as a failure: one stream refused out
        // of four is a slower measurement, not an unusable one.
        return bytes == 0 && failure is not null
            ? new ThroughputResult(endpoint, direction, streams, 0, elapsed, false, failure.Message, failure.Kind)
            : new ThroughputResult(
                endpoint, direction, streams, bytes, elapsed,
                !cancellationToken.IsCancellationRequested);
    }

    private async Task<(long Bytes, TransferFailure? Failure)> RunStreamAsync(
        string endpoint, TransferDirection direction, CancellationToken stopToken)
    {
        try
        {
            return (await _transfer(endpoint, direction, stopToken), null);
        }
        catch (OperationCanceledException)
        {
            // Expected: this is how every stream ends when the window closes.
            return (0, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or SocketException or IOException)
        {
            return (0, new TransferFailure(exception.Message, Classify(exception)));
        }
    }

    internal static DiagnosticFailureKind Classify(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: not null } => DiagnosticFailureKind.ConnectionRefused,
        HttpRequestException { InnerException: SocketException socket } => NetworkDiagnosticService.ClassifySocketError(socket.SocketErrorCode),
        SocketException socket => NetworkDiagnosticService.ClassifySocketError(socket.SocketErrorCode),
        _ => DiagnosticFailureKind.LocalApiFailure
    };

    private sealed record TransferFailure(string Message, DiagnosticFailureKind Kind);

    // Shared client: a new HttpClient per stream would exhaust sockets on a long run.
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    // Cancellation is the normal end of a transfer, not a failure, so the bytes already moved are
    // returned rather than thrown away with the exception.
    private static async Task<long> TransferAsync(
        string endpoint, TransferDirection direction, CancellationToken cancellationToken)
    {
        if (direction == TransferDirection.Upload)
        {
            var content = new CountingContent(cancellationToken);
            try
            {
                using var response = await Client.PostAsync(endpoint, content, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The window closed mid-upload; content.Bytes still holds what was sent.
            }

            return content.Bytes;
        }

        long total = 0;
        try
        {
            using var download = await Client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            download.EnsureSuccessStatusCode();
            await using var stream = await download.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
            }
        }
        catch (OperationCanceledException)
        {
            // Same: the measurement window ended, the bytes counted so far are the measurement.
        }

        return total;
    }

    /// <summary>Streams incompressible-looking filler upwards until the window closes, counting what left the machine.</summary>
    private sealed class CountingContent(CancellationToken stopToken) : HttpContent
    {
        private readonly byte[] _chunk = Filler();
        private long _bytes;

        public long Bytes => Interlocked.Read(ref _bytes);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            // Both tokens end the upload: the measurement window and the request itself.
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(stopToken, cancellationToken);
            while (!stop.IsCancellationRequested)
            {
                await stream.WriteAsync(_chunk, stop.Token);
                Interlocked.Add(ref _bytes, _chunk.Length);
            }
        }

        // The legacy overload has no token of its own, so it relies on the window token alone.
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, stopToken);

        private static byte[] Filler()
        {
            var chunk = new byte[64 * 1024];
            Random.Shared.NextBytes(chunk);
            return chunk;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
