using System.Buffers.Binary;
using System.Net;
using SockTuner.Models;
using SockTuner.Services.Collection;

namespace SockTuner.Tests;

public sealed class DnsBenchmarkTests
{
    private static readonly IReadOnlyList<string> OneHost = ["example.com"];

    [Fact]
    public async Task TheFastestUsableResolverIsRanked()
    {
        var probe = Probe(new Dictionary<string, double?>
        {
            ["1.1.1.1"] = 12,
            ["8.8.8.8"] = 40
        });

        var report = await probe.RunAsync(
            [new("Cloudflare", "1.1.1.1"), new("Google", "8.8.8.8")], OneHost, 3, TimeSpan.FromSeconds(1), default);

        Assert.Equal("1.1.1.1", report.Fastest?.Resolver.Address);
        Assert.Equal(3, report.Fastest?.Answered);
    }

    [Fact]
    public async Task AResolverThatNeverAnswersIsReportedRatherThanDropped()
    {
        // "It did not answer" is the single most useful thing a benchmark can say about a resolver
        // someone is about to switch to.
        var probe = Probe(new Dictionary<string, double?> { ["1.1.1.1"] = 10, ["203.0.113.9"] = null });

        var report = await probe.RunAsync(
            [new("Cloudflare", "1.1.1.1"), new("Dead", "203.0.113.9")], OneHost, 2, TimeSpan.FromSeconds(1), default);

        var dead = report.Results.Single(result => result.Resolver.Address == "203.0.113.9");
        Assert.Equal(0, dead.Answered);
        Assert.False(dead.Usable);
        Assert.NotEqual(dead, report.Fastest);
    }

    [Fact]
    public async Task ASmallGainOverTheResolverInUseIsNotPresentedAsAnImprovement()
    {
        // Run-to-run variation on a DNS query is easily a couple of milliseconds. Reporting a 2 ms
        // "win" as a reason to change would be noise dressed as a finding.
        var probe = Probe(new Dictionary<string, double?> { ["1.1.1.1"] = 18, ["192.168.1.1"] = 20 });

        var report = await probe.RunAsync(
            [new("Cloudflare", "1.1.1.1"), new("Router", "192.168.1.1", InUse: true)],
            OneHost, 3, TimeSpan.FromSeconds(1), default);

        Assert.Contains("not a reason to change", report.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWorthwhileGainSaysWhatItActuallyImproves()
    {
        var probe = Probe(new Dictionary<string, double?> { ["1.1.1.1"] = 10, ["192.168.1.1"] = 90 });

        var report = await probe.RunAsync(
            [new("Cloudflare", "1.1.1.1"), new("Router", "192.168.1.1", InUse: true)],
            OneHost, 3, TimeSpan.FromSeconds(1), default);

        Assert.Equal(80, report.ImprovementMs);
        Assert.Contains("does not change the latency of a session already connected", report.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoResolverAnsweringIsCalledOutAsABlockedPort()
    {
        var probe = Probe(new Dictionary<string, double?> { ["1.1.1.1"] = null });

        var report = await probe.RunAsync([new("Cloudflare", "1.1.1.1")], OneHost, 2, TimeSpan.FromSeconds(1), default);

        Assert.Null(report.Fastest);
        Assert.Contains("port 53", report.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidAddressFailsThatResolverOnly()
    {
        var probe = Probe(new Dictionary<string, double?> { ["1.1.1.1"] = 10 });

        var report = await probe.RunAsync(
            [new("Broken", "not-an-ip"), new("Cloudflare", "1.1.1.1")], OneHost, 1, TimeSpan.FromSeconds(1), default);

        Assert.NotNull(report.Results.Single(result => result.Resolver.Name == "Broken").Error);
        Assert.Equal("1.1.1.1", report.Fastest?.Resolver.Address);
    }

    [Fact]
    public void TheQueryIsAWellFormedSingleQuestionPacket()
    {
        var packet = DnsBenchmarkProbe.BuildQuery(0xABCD, "example.com");

        Assert.Equal(0xABCD, BinaryPrimitives.ReadUInt16BigEndian(packet));
        Assert.Equal(0x01, packet[2]);                                          // recursion desired
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4))); // exactly one question
        Assert.Equal(7, packet[12]);                                            // "example"
        Assert.Equal(3, packet[20]);                                            // "com"
        Assert.Equal(0, packet[24]);                                            // root label
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(25))); // QTYPE A
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(27))); // QCLASS IN
    }

    [Fact]
    public void TheMedianOfAnEvenNumberOfSamplesIsTheMidpoint()
    {
        Assert.Equal(15, DnsBenchmarkProbe.Median([10, 10, 20, 20]));
        Assert.Equal(20, DnsBenchmarkProbe.Median([10, 20, 30]));
    }

    [Fact]
    public async Task CancellationStopsTheRun()
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new DnsBenchmarkProbe((_, _, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<double?>(1);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.RunAsync(
            [new("Cloudflare", "1.1.1.1")], OneHost, 3, TimeSpan.FromSeconds(1), cancellation.Token));
    }

    private static DnsBenchmarkProbe Probe(Dictionary<string, double?> latencyByAddress) =>
        new((address, _, _, _) => Task.FromResult(
            latencyByAddress.TryGetValue(address.ToString(), out var value) ? value : null));
}
