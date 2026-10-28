using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SockTuner.Models;
using SockTuner.Services.Collection;

namespace SockTuner.Tests;

public class NatDiagnosticProbeTests
{
    [Theory]
    [InlineData(false, false, NatFilteringObservation.AlternatePortReceived, NatMappingObservation.SameOnTestedDestinations)]
    [InlineData(true, false, NatFilteringObservation.AlternatePortFiltered, NatMappingObservation.SameOnTestedDestinations)]
    [InlineData(true, true, NatFilteringObservation.AlternatePortFiltered, NatMappingObservation.ChangedOnTestedDestinations)]
    public async Task MeasuresOnlyTheObservedDimensions(bool filtered, bool differentMapping,
        NatFilteringObservation filtering, NatMappingObservation mapping)
    {
        await using var server = new TestServers(filtered, differentMapping);
        var result = await new NatDiagnosticProbe(TimeSpan.FromMilliseconds(150))
            .RunAsync(0, server.Primary, server.Secondary, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(NatProbeFailure.None, result.Failure);
        Assert.Equal(filtering, result.Filtering);
        Assert.Equal(mapping, result.Mapping);
        Assert.True(result.LocalPort > 0);
        Assert.Equal("203.0.113.7:49000", result.PublicEndpoint!.ToString());
        if (filtered) Assert.True(server.ChangesBeforeContact >= 2);
    }

    [Fact]
    public async Task ThirdDestinationWithDifferentMappingIsDetected()
    {
        await using var server = new TestServers(tertiaryDifferent: true);
        var result = await new NatDiagnosticProbe(TimeSpan.FromMilliseconds(150))
            .RunAsync(0, server.Primary, [server.Secondary, server.Tertiary], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(NatMappingObservation.ChangedOnTestedDestinations, result.Mapping);
    }

    [Fact]
    public async Task SilentThirdDestinationDoesNotHideAStableMapping()
    {
        await using var server = new TestServers();
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var result = await new NatDiagnosticProbe(TimeSpan.FromMilliseconds(100))
            .RunAsync(0, server.Primary, [server.Secondary, (IPEndPoint)silent.Client.LocalEndPoint!], CancellationToken.None);
        Assert.Equal(NatMappingObservation.SameOnTestedDestinations, result.Mapping);
    }

    [Fact]
    public async Task IgnoredChangeRequestIsInconclusiveNotRestricted()
    {
        await using var server = new TestServers(ignoreChange: true);
        var result = await new NatDiagnosticProbe(TimeSpan.FromMilliseconds(100))
            .RunAsync(0, server.Primary, server.Secondary, CancellationToken.None);
        Assert.Equal(NatFilteringObservation.Inconclusive, result.Filtering);
    }

    [Fact]
    public async Task WrongReplySourceDoesNotBecomeAnAcceptanceVerdict()
    {
        await using var server = new TestServers(wrongChangeSource: true);
        var result = await new NatDiagnosticProbe(TimeSpan.FromMilliseconds(100))
            .RunAsync(0, server.Primary, server.Secondary, CancellationToken.None);
        Assert.Equal(NatFilteringObservation.Inconclusive, result.Filtering);
    }

    [Fact]
    public async Task ChangingBaselineInvalidatesMappingComparisonAndFiltering()
    {
        await using var server = new TestServers(unstableMapping: true);
        var result = await new NatDiagnosticProbe(TimeSpan.FromMilliseconds(100))
            .RunAsync(0, server.Primary, server.Secondary, CancellationToken.None);
        Assert.Equal(NatMappingObservation.NotMeasured, result.Mapping);
        Assert.Equal(NatFilteringObservation.Inconclusive, result.Filtering);
    }

    [Fact]
    public async Task MissingOtherAddressMeansUnsupportedNotFullCone()
    {
        await using var server = new TestServers(advertiseOther: false);
        var result = await new NatDiagnosticProbe(TimeSpan.FromMilliseconds(100))
            .RunAsync(0, server.Primary, server.Secondary, CancellationToken.None);
        Assert.Equal(NatFilteringObservation.Unsupported, result.Filtering);
    }

    [Fact]
    public async Task NoBaselineIsNotABlockedPortVerdict()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = (IPEndPoint)silent.Client.LocalEndPoint!;
        var result = await new NatDiagnosticProbe(TimeSpan.FromMilliseconds(50))
            .RunAsync(0, target, target, CancellationToken.None);
        Assert.Equal(NatProbeFailure.NoBaseline, result.Failure);
        Assert.Equal(NatFilteringObservation.NotMeasured, result.Filtering);
    }

    [Fact]
    public async Task OccupiedPortIsNeverSharedOrReplaced()
    {
        using var occupied = new UdpClient(AddressFamily.InterNetwork);
        occupied.ExclusiveAddressUse = true;
        occupied.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)occupied.Client.LocalEndPoint!).Port;
        var target = new IPEndPoint(IPAddress.Loopback, 9);
        var result = await new NatDiagnosticProbe().RunAsync(port, target, target, CancellationToken.None);
        Assert.Equal(NatProbeFailure.PortUnavailable, result.Failure);
        Assert.Equal(port, result.LocalPort);
        Assert.Null(result.PublicEndpoint);
    }

    [Fact]
    public async Task CancellationPropagatesAndReleasesSocket()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = (IPEndPoint)silent.Client.LocalEndPoint!;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NatDiagnosticProbe()
            .RunAsync(0, target, target, cancellation.Token));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task RejectsInvalidPortsBeforeNetworkTraffic(int port) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new NatDiagnosticProbe().RunAsync(port, CancellationToken.None));

    [Fact]
    public void ParserValidatesTransactionCookieLengthsAndAddress()
    {
        var request = StunMessage.Request(false);
        var response = Reply(request, 49000, 3478);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 49000), StunMessage.Parse(response, request)!.Mapped);
        Assert.Equal(3478, StunMessage.Parse(response, request)!.Other!.Port);
        Assert.Null(StunMessage.Parse(response, StunMessage.Request(false)));
        for (var size = 0; size < response.Length; size++)
            Assert.Null(StunMessage.Parse(response.AsSpan(0, size), request));
        foreach (var offset in new[] { 0, 2, 4, 8, 22, 25 })
        {
            var broken = response.ToArray();
            broken[offset] ^= 0x80;
            Assert.Null(StunMessage.Parse(broken, request));
        }
    }

    [Fact]
    public void BuildsAnExplicitChangePortRequest()
    {
        var request = StunMessage.Request(true);
        Assert.Equal(28, request.Length);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(20)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(24)));
    }

    [Fact]
    public void PresetsHaveValidPortsAndPublisherSources()
    {
        Assert.Equal(0, GamePortPresets.All[0].SuggestedPort);
        Assert.All(GamePortPresets.All.Skip(1), preset =>
        {
            Assert.InRange(preset.SuggestedPort, 1, 65535);
            Assert.Equal("https", new Uri(preset.SourceUrl).Scheme);
            Assert.NotEmpty(preset.PublishedUdpPorts);
            Assert.NotEmpty(preset.Scope);
        });
        Assert.Contains(NatSafetyGuidance.All, row => row.Action == "Avoid DMZ and disabling firewalls");
    }

    private static byte[] Reply(byte[] request, int mappedPort, int? otherPort)
    {
        var reply = new byte[otherPort is null ? 32 : 44];
        BinaryPrimitives.WriteUInt16BigEndian(reply, 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), (ushort)(reply.Length - 20));
        request.AsSpan(4, 16).CopyTo(reply.AsSpan(4));
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(20), 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(22), 8);
        reply[25] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(26), (ushort)(mappedPort ^ 0x2112));
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(28), 0xcb007107 ^ 0x2112a442);
        if (otherPort is { } port)
        {
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(32), 0x802c);
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(34), 8);
            reply[37] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(38), (ushort)port);
            IPAddress.Loopback.GetAddressBytes().CopyTo(reply, 40);
        }
        return reply;
    }

    private sealed class TestServers : IAsyncDisposable
    {
        private readonly UdpClient _primary = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly UdpClient _alternate = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly UdpClient _secondary = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly UdpClient _tertiary = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task[] _loops;
        private int _contacted;
        public int ChangesBeforeContact { get; private set; }
        public IPEndPoint Primary => (IPEndPoint)_primary.Client.LocalEndPoint!;
        public IPEndPoint Tertiary => (IPEndPoint)_tertiary.Client.LocalEndPoint!;
        public IPEndPoint Secondary => (IPEndPoint)_secondary.Client.LocalEndPoint!;

        public TestServers(bool filtered = false, bool differentMapping = false, bool ignoreChange = false,
            bool advertiseOther = true, bool wrongChangeSource = false, bool unstableMapping = false, bool tertiaryDifferent = false)
        {
            var primaryBindings = 0;
            _loops = new[] { _primary, _alternate, _secondary, _tertiary }.Select(socket => Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var request = await socket.ReceiveAsync(_stop.Token);
                        var change = request.Buffer.Length == 28;
                        if (socket == _alternate) Interlocked.Exchange(ref _contacted, 1);
                        if (change && Volatile.Read(ref _contacted) == 0) ChangesBeforeContact++;
                        if (change && (ignoreChange || (filtered && Volatile.Read(ref _contacted) == 0))) continue;
                        var source = change && !wrongChangeSource ? _alternate : socket;
                        if (socket == _primary && !change) primaryBindings++;
                        var mappedPort = socket == _primary && unstableMapping && primaryBindings > 1
                            ? 49002 : socket == _secondary && differentMapping ? 49001 : socket == _tertiary && tertiaryDifferent ? 49003 : 49000;
                        var response = Reply(request.Buffer, mappedPort,
                            advertiseOther ? ((IPEndPoint)_alternate.Client.LocalEndPoint!).Port : null);
                        await source.SendAsync(response, request.RemoteEndPoint, _stop.Token);
                    }
                }
                catch (OperationCanceledException) { }
            })).ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await Task.WhenAll(_loops);
            _primary.Dispose(); _alternate.Dispose(); _secondary.Dispose(); _tertiary.Dispose(); _stop.Dispose();
        }
    }
}

public class NatGamerReadingTests
{
    [Theory]
    [InlineData(NatMappingObservation.ChangedOnTestedDestinations, NatFilteringObservation.AlternatePortReceived, "Strict-like")]
    [InlineData(NatMappingObservation.SameOnTestedDestinations, NatFilteringObservation.AlternatePortReceived, "Open-like")]
    [InlineData(NatMappingObservation.SameOnTestedDestinations, NatFilteringObservation.AlternatePortFiltered, "Moderate-like")]
    [InlineData(NatMappingObservation.SameOnTestedDestinations, NatFilteringObservation.Inconclusive, "Stable public port")]
    [InlineData(NatMappingObservation.NotMeasured, NatFilteringObservation.AlternatePortReceived, "Not enough evidence")]
    public void ReadingFollowsMeasuredDimensionsOnly(NatMappingObservation mapping, NatFilteringObservation filtering, string expected) =>
        Assert.StartsWith(expected, NatGamerReading.Of(new(1, null, mapping, filtering)));
}
