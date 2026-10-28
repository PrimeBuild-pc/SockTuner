using System.Net;
using SockTuner.Models;
using SockTuner.Services.Collection;

namespace SockTuner.Tests;

public class PeerNatPairingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static PairingCode Sample(string ip = "203.0.113.7", int port = 3074) =>
        new(new IPEndPoint(IPAddress.Parse(ip), port), [1, 2, 3, 4, 5, 6, 7, 8], Now.AddMinutes(3));

    [Fact]
    public void CodeRoundTripsThroughText()
    {
        var parsed = PairingCode.Parse(Sample().Format(), Now, out var error);
        Assert.Equal(PairingError.None, error);
        Assert.Equal("203.0.113.7:3074", parsed!.Endpoint.ToString());
        Assert.Equal(Sample().Secret, parsed.Secret);
        Assert.Equal(Sample().Start, parsed.Start);
    }

    [Theory]
    [InlineData("10.0.0.5")] [InlineData("127.0.0.1")] [InlineData("192.168.1.20")] [InlineData("172.16.4.4")]
    [InlineData("169.254.1.1")] [InlineData("100.64.0.9")] [InlineData("0.0.0.0")] [InlineData("224.0.0.1")]
    public void NonPublicTargetsAreRejected(string ip)
    {
        Assert.Null(PairingCode.Parse(Sample(ip).Format(), Now, out var error));
        Assert.Equal(PairingError.NotPublic, error);
    }

    [Fact]
    public void ExpiredPortlessAndCorruptCodesAreRejected()
    {
        Assert.Null(PairingCode.Parse(Sample().Format(), Now.AddMinutes(4), out var expired));
        Assert.Equal(PairingError.Expired, expired);
        Assert.Null(PairingCode.Parse(Sample(port: 0).Format(), Now, out var portless));
        Assert.Equal(PairingError.Malformed, portless);
        var text = Sample().Format();
        var flipped = text[..^3] + (text[^3] == 'A' ? 'B' : 'A') + text[^2..];
        Assert.Null(PairingCode.Parse(flipped, Now, out var corrupt));
        Assert.Equal(PairingError.Checksum, corrupt);
        foreach (var junk in new[] { null, "", "hello", "STP1-", "STP1-!!!!", "STP2-" + text[5..] })
            Assert.NotEqual(PairingError.None, Run(junk));
        PairingError Run(string? s) { PairingCode.Parse(s, Now, out var e); return e; }
    }

    [Fact]
    public void AStartFarInTheFutureIsATypoNotAWait()
    {
        var far = Sample() with { Start = Now.AddYears(3) };
        Assert.Null(PairingCode.Parse(far.Format(), Now, out var error));
        Assert.Equal(PairingError.TooFar, error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BasicModeShowsTheBasicListAndNeverAnEmptyGroup(bool advanced)
    {
        // Rows: header, Dashboard (basic), header, Interfaces (advanced), header, Preferences (always hidden).
        (string, bool)[] items = [("Overview", true), ("Dashboard", false), ("Records", true), ("Interfaces", false), ("Settings", true), ("Preferences", false)];
        var basic = new HashSet<string> { "Dashboard" };

        var visible = SectionVisibility.Compute(items, advanced, basic, "Preferences");

        Assert.Equal(advanced ? new[] { true, true, true, true, false, false } : new[] { true, true, false, false, false, false }, visible);
    }

    [Fact]
    public void PacketsRequireTheSharedSecretAndAKnownRound()
    {
        byte[] secret = [1, 2, 3, 4, 5, 6, 7, 8], other = [8, 7, 6, 5, 4, 3, 2, 1];
        var packet = PeerPacket.Create(secret, 2, 9);
        Assert.Equal(PeerPacket.Length, packet.Length);
        Assert.True(PeerPacket.TryParse(packet, secret, out var round));
        Assert.Equal(2, round);
        Assert.False(PeerPacket.TryParse(packet, other, out _));
        for (var size = 0; size < packet.Length; size++) Assert.False(PeerPacket.TryParse(packet.AsSpan(0, size), secret, out _));
        for (var i = 0; i < packet.Length; i++)
        {
            var broken = packet.ToArray();
            broken[i] ^= 0x01;
            Assert.False(PeerPacket.TryParse(broken, secret, out _));
        }
        Assert.False(PeerPacket.TryParse(PeerPacket.Create(secret, 9, 0), secret, out _));
    }
}

public class PeerNatReadingTests
{
    [Theory]
    [InlineData(PeerNatFailure.None, true, true, "Open-like")]
    [InlineData(PeerNatFailure.None, false, true, "Moderate-like")]
    [InlineData(PeerNatFailure.None, false, false, "Nothing arrived")]
    [InlineData(PeerNatFailure.SameNetwork, true, true, "Both codes show the same public IP")]
    [InlineData(PeerNatFailure.PortUnavailable, false, false, "Local port unavailable")]
    public void VerdictFollowsWhatArrived(PeerNatFailure failure, bool cold, bool punch, string expected) =>
        Assert.StartsWith(expected, PeerNatReading.Of(new(PeerRole.Host, 3074, failure, cold, punch)));

    [Fact]
    public void ColdTrafficWithoutPunchStillReadsOpen() =>
        Assert.StartsWith("Open-like", PeerNatReading.Of(new(PeerRole.Guest, 1, PeerNatFailure.None, true, false)));

    [Fact]
    public void EveryFailureHasItsOwnText() =>
        Assert.Equal(Enum.GetValues<PeerNatFailure>().Length,
            Enum.GetValues<PeerNatFailure>().Select(f => PeerNatReading.Of(new(PeerRole.Host, 1, f))).Distinct().Count());
}

public class QuickStartTests
{
    [Theory]
    [InlineData(SockTuner.Services.Diagnosis.PlayabilityGrade.Good, "Good")]
    [InlineData(SockTuner.Services.Diagnosis.PlayabilityGrade.Playable, "Borderline")]
    [InlineData(SockTuner.Services.Diagnosis.PlayabilityGrade.Poor, "Not playable")]
    [InlineData(null, "Not checked")]
    public void EveryGradeHasOneShortAnswer(SockTuner.Services.Diagnosis.PlayabilityGrade? grade, string expected) =>
        Assert.StartsWith(expected, QuickStart.Verdict(grade));
}
