using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using SockTuner.Models;
using SockTuner.Services.Collection;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

public sealed class UdpQualityTests
{
    private static string Key() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static UdpQualityProbe Probe() => new(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(100));

    [Fact]
    public async Task EchoAndReceiverSummaryMeasureBothDirections()
    {
        var key = Key();
        using var server = new UdpQualityResponder(new(IPAddress.Loopback, 0), key);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var running = server.RunAsync(stop.Token);
        try
        {
            var result = await Probe().RunAsync("127.0.0.1", server.Endpoint.Port, key, new(1, 20), null, stop.Token);
            Assert.Equal(20, result.Sent);
            Assert.Equal(20, result.ServerReceived);
            Assert.Equal(20, result.Received);
            Assert.Equal(0, result.UploadLost);
            Assert.Equal(0, result.DownloadLost);
            Assert.NotNull(result.MedianMs);
            Assert.NotNull(result.P95Ms);
            Assert.NotNull(result.JitterMs);
            var json = System.Text.Json.JsonSerializer.Serialize(result);
            Assert.DoesNotContain(key, json);
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
    }

    [Fact]
    public async Task ReceiptBitmapSeparatesUploadLossFromDownloadLossAndDoesNotCountDuplicatesAsReplies()
    {
        var key = Key();
        using var server = new UdpQualityResponder(new(IPAddress.Loopback, 0), key);
        using var relay = new Relay(server.Endpoint, key, dropUpload: true, dropDownload: true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var running = server.RunAsync(stop.Token);
        var relaying = relay.RunAsync(stop.Token);
        try
        {
            var report = await Probe().RunAsync("127.0.0.1", relay.Endpoint.Port, key, new(1, 20), null, stop.Token);
            Assert.Equal(1, report.UploadLost);
            Assert.Equal(1, report.DownloadLost);
            Assert.Equal(19, report.ServerReceived);
            Assert.Equal(18, report.Received);
            Assert.Equal(1, report.Duplicates);
            Assert.Equal(1, report.Reordered);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relaying);
        }
    }

    [Fact]
    public async Task MissingFinalSummaryIsInconclusiveRatherThanZeroLoss()
    {
        var key = Key();
        using var server = new UdpQualityResponder(new(IPAddress.Loopback, 0), key);
        using var relay = new Relay(server.Endpoint, key, dropSummary: true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var running = server.RunAsync(stop.Token);
        var relaying = relay.RunAsync(stop.Token);
        try
        {
            var error = await Assert.ThrowsAsync<System.IO.IOException>(() => Probe().RunAsync(
                "127.0.0.1", relay.Endpoint.Port, key, new(1, 20), null, stop.Token));
            Assert.Contains("summary", error.Message);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relaying);
        }
    }

    [Fact]
    public async Task LateEchoesAreDeliveredPacketsAndAFinalSummaryCanBeRetried()
    {
        var key = Key();
        using var server = new UdpQualityResponder(new(IPAddress.Loopback, 0), key);
        using var relay = new Relay(server.Endpoint, key, delayEcho: true, dropFirstSummary: true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var running = server.RunAsync(stop.Token);
        var relaying = relay.RunAsync(stop.Token);
        try
        {
            var report = await Probe().RunAsync("127.0.0.1", relay.Endpoint.Port, key, new(1, 20, 128, 30), null, stop.Token);
            Assert.Equal(20, report.Received);
            Assert.Equal(0, report.UploadLost);
            Assert.Equal(0, report.DownloadLost);
            Assert.True(report.Late >= 1);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relaying);
        }
    }

    [Fact]
    public async Task WrongKeyDoesNotProduceAResponderVerdict()
    {
        using var server = new UdpQualityResponder(new(IPAddress.Loopback, 0), Key());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var running = server.RunAsync(stop.Token);
        try
        {
            await Assert.ThrowsAsync<System.IO.IOException>(() => Probe().RunAsync(
                "127.0.0.1", server.Endpoint.Port, Key(), new(1, 20), null, stop.Token));
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
    }

    [Fact]
    public async Task CancellationStopsTheClientAndItsReceiver()
    {
        var key = Key();
        using var server = new UdpQualityResponder(new(IPAddress.Loopback, 0), key);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var clientStop = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var running = server.RunAsync(stop.Token);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Probe().RunAsync(
                "127.0.0.1", server.Endpoint.Port, key, new(), null, clientStop.Token));
        }
        finally { stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running); }
    }

    [Fact]
    public void AuthenticationRejectsCorruptionAndOversizedOrForeignPackets()
    {
        var key = Convert.FromHexString(Key());
        var data = UdpQualityProtocol.Create(UdpQualityMessage.Probe, Guid.NewGuid(), key, 12);
        Assert.True(UdpQualityProtocol.TryRead(data, key, out _, out _, out var seq, out _));
        Assert.Equal(12, seq);
        data[21] ^= 1;
        Assert.False(UdpQualityProtocol.TryRead(data, key, out _, out _, out _, out _));
        Assert.False(UdpQualityProtocol.TryRead(new byte[95], key, out _, out _, out _, out _));
        Assert.False(UdpQualityProtocol.TryRead(new byte[1201], key, out _, out _, out _, out _));
        Assert.False(UdpQualityProtocol.TryRead(new byte[128], key, out _, out _, out _, out _));
        Assert.Throws<ArgumentException>(() => UdpQualityProtocol.ParseKey("not-a-key"));
    }

    [Theory]
    [InlineData(0, 20, 128)]
    [InlineData(60, 50, 128)]
    [InlineData(30, 20, 1400)]
    [InlineData(30, 0, 128)]
    public void TrafficIsBounded(int seconds, int rate, int size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UdpQualityOptions(seconds, rate, size).Validate());

    [Fact]
    public void ComparisonRequiresMatchingContextAndConfirmedConditions()
    {
        var before = Report(10);
        var after = Report(3);
        Assert.Equal(ApplyOutcome.Improved, UdpQualityComparison.Compare(before, after, true).Outcome);
        Assert.Equal(ApplyOutcome.NotComparable, UdpQualityComparison.Compare(before, after, false).Outcome);
        Assert.Equal(ApplyOutcome.NotComparable, UdpQualityComparison.Compare(before, after with { Endpoint = "192.0.2.2:52345" }, true).Outcome);
        Assert.Equal(ApplyOutcome.NotComparable, UdpQualityComparison.Compare(before, after with { LinkSpeed = 100000000 }, true).Outcome);
        Assert.Equal(ApplyOutcome.NotComparable, UdpQualityComparison.Compare(before, after with { SchedulingDelays = 5 }, true).Outcome);
        Assert.Equal(ApplyOutcome.NoMeasurableChange, UdpQualityComparison.Compare(before, before, true).Outcome);
        Assert.Equal(ApplyOutcome.Worse, UdpQualityComparison.Compare(after, before, true).Outcome);
    }

    private static UdpQualityReport Report(double rtt) => new(DateTimeOffset.UtcNow, "192.0.2.1:52345", new(1, 20),
        "192.0.2.10", "adapter", 1000000000, false, 20, 20, 20, 0, 0, 0, 0, 1, Enumerable.Repeat(rtt, 20).ToArray());

    private sealed class Relay(IPEndPoint server, string keyText, bool dropUpload = false,
        bool dropDownload = false, bool dropSummary = false, bool delayEcho = false, bool dropFirstSummary = false) : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        internal IPEndPoint Endpoint => (IPEndPoint)_udp.Client.LocalEndPoint!;
        public async Task RunAsync(CancellationToken token)
        {
            var key = Convert.FromHexString(keyText);
            IPEndPoint? client = null;
            byte[]? held = null;
            var summaries = 0;
            while (true)
            {
                var packet = await _udp.ReceiveAsync(token);
                if (!UdpQualityProtocol.TryRead(packet.Buffer, key, out var type, out _, out var seq, out _)) continue;
                var downstream = packet.RemoteEndPoint.Equals(server);
                if (!downstream) client = packet.RemoteEndPoint;
                if (dropUpload && type == UdpQualityMessage.Probe && seq == 0) continue;
                if (dropDownload && type == UdpQualityMessage.Echo && seq == 1) continue;
                if (dropSummary && type == UdpQualityMessage.Summary) continue;
                if (dropFirstSummary && type == UdpQualityMessage.Summary && summaries++ == 0) continue;
                var target = downstream ? client! : server;
                if (delayEcho && type == UdpQualityMessage.Echo && seq == 5) await Task.Delay(80, token);
                if (dropDownload && type == UdpQualityMessage.Echo && seq == 3) { held = packet.Buffer; continue; }
                await _udp.SendAsync(packet.Buffer, target, token);
                if (dropDownload && type == UdpQualityMessage.Echo && seq == 2) await _udp.SendAsync(packet.Buffer, target, token);
                if (held is not null && type == UdpQualityMessage.Echo && seq == 4)
                {
                    await _udp.SendAsync(held, target, token);
                    held = null;
                }
            }
        }
        public void Dispose() => _udp.Dispose();
    }
}
