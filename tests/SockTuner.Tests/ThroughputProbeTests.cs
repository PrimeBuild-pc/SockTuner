using SockTuner.Models;
using SockTuner.Services.Collection;

namespace SockTuner.Tests;

/// <summary>
/// The probe is exercised against a fake transfer, so the suite never moves bytes over a real
/// network or picks an endpoint on the host.
/// </summary>
public sealed class ThroughputProbeTests
{
    [Fact]
    public async Task EveryStreamContributesToTheTotal()
    {
        var probe = new ThroughputProbe((_, _, token) => Transfer(1_000, token));

        var result = await probe.RunAsync("http://fake/download", TransferDirection.Download, 4, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Equal(4_000, result.Bytes);
        Assert.Equal(4, result.Streams);
        Assert.True(result.Completed);
        Assert.Null(result.Error);
        Assert.True(result.BitsPerSecond > 0);
    }

    [Fact]
    public async Task CallerCancellation_KeepsTheMeasuredRateButMarksItIncomplete()
    {
        using var cancellation = new CancellationTokenSource();
        // A real transfer reports the bytes it moved even when the window closes under it.
        var probe = new ThroughputProbe(async (_, _, _) =>
        {
            await cancellation.CancelAsync();
            return 500L;
        });

        var result = await probe.RunAsync("http://fake/download", TransferDirection.Download, 1, TimeSpan.FromSeconds(5), cancellation.Token);

        Assert.False(result.Completed);
        Assert.Equal(500, result.Bytes);
    }

    [Fact]
    public async Task AllStreamsFailing_IsReportedAsAFailureWithItsKind()
    {
        var probe = new ThroughputProbe((_, _, _) =>
            Task.FromException<long>(new HttpRequestException("refused", null, System.Net.HttpStatusCode.Forbidden)));

        var result = await probe.RunAsync("http://fake/download", TransferDirection.Download, 2, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Equal(0, result.Bytes);
        Assert.Equal("refused", result.Error);
        Assert.Equal(DiagnosticFailureKind.ConnectionRefused, result.FailureKind);
    }

    [Fact]
    public async Task OneStreamFailing_StillReportsWhatTheOthersMoved()
    {
        var stream = 0;
        var probe = new ThroughputProbe((_, _, token) => Interlocked.Increment(ref stream) == 1
            ? Task.FromException<long>(new HttpRequestException("refused"))
            : Transfer(2_000, token));

        var result = await probe.RunAsync("http://fake/download", TransferDirection.Download, 3, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(4_000, result.Bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ThroughputProbe.MaximumStreams + 1)]
    public async Task StreamCountIsBounded(int streams) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ThroughputProbe((_, _, _) => Task.FromResult(0L))
            .RunAsync("http://fake", TransferDirection.Download, streams, TimeSpan.FromSeconds(1), CancellationToken.None));

    [Fact]
    public async Task DurationIsBounded() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ThroughputProbe((_, _, _) => Task.FromResult(0L))
            .RunAsync("http://fake", TransferDirection.Download, 1, ThroughputProbe.MaximumDuration + TimeSpan.FromSeconds(1), CancellationToken.None));

    [Fact]
    public void RateFormattingScalesWithMagnitude()
    {
        Assert.Equal("2.5 Mbit/s", ThroughputResult.FormatRate(2_500_000));
        Assert.Equal("1.00 Gbit/s", ThroughputResult.FormatRate(1_000_000_000));
    }

    private static async Task<long> Transfer(long bytes, CancellationToken token)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        return bytes;
    }
}
