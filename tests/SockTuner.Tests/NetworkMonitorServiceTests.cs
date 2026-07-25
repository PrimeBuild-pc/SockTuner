using System.Net.NetworkInformation;
using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class NetworkMonitorServiceTests
{
    [Fact]
    public async Task RunAsync_ProbesTargetsConcurrentlyAndBoundsNewestSamples()
    {
        var active = 0;
        var maximumActive = 0;
        var service = new NetworkMonitorService(async (target, _, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            await Task.Delay(2, cancellationToken);
            Interlocked.Decrement(ref active);
            return new MonitorSample(DateTimeOffset.UtcNow, target.Label, target.Target, 5, MonitorSampleKind.Reply, null);
        });

        var report = await service.RunAsync(
            [new("Gateway", "192.0.2.1"), new("Game", "198.51.100.1")],
            TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1),
            2, null, CancellationToken.None);

        Assert.Equal(2, report.Samples.Count);
        Assert.Equal(2, report.Summaries.Count);
        Assert.True(report.TotalSampleCount >= report.Samples.Count);
        Assert.True(maximumActive >= 2);
    }

    [Fact]
    public async Task RunAsync_PreCanceled_DoesNotProbe()
    {
        var calls = 0;
        var service = new NetworkMonitorService((target, timeout, token) =>
        {
            calls++;
            return Task.FromResult(new MonitorSample(DateTimeOffset.UtcNow, target.Label, target.Target, 1, MonitorSampleKind.Reply, null));
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(
            [new("Game", "198.51.100.1")], TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(1), 10, null, cancellation.Token));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(IPStatus.TimedOut, MonitorSampleKind.NoReply)]
    [InlineData(IPStatus.DestinationProhibited, MonitorSampleKind.Blocked)]
    [InlineData(IPStatus.DestinationHostUnreachable, MonitorSampleKind.Unreachable)]
    [InlineData(IPStatus.DestinationUnreachable, MonitorSampleKind.Unreachable)]
    [InlineData(IPStatus.BadOption, MonitorSampleKind.LocalError)]
    public void Classify_DistinguishesNoReplyBlockedUnreachableAndLocalErrors(IPStatus status, MonitorSampleKind expected)
    {
        Assert.Equal(expected, NetworkMonitorService.Classify(status));
    }
}
