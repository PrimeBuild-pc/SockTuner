using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

/// <summary>
/// The QoS policy write path, checked entirely against its specification. No policy is created,
/// changed or removed by this suite.
/// </summary>
public sealed class QosPolicySettingTests
{
    private static QosPolicySpecification Specification() => new();

    private const string Name = QosPolicySpecification.NamePrefix + "Valorant";

    private static string Value(
        int dscp = QosPolicySpecification.ExpeditedForwarding,
        string app = "VALORANT-Win64-Shipping.exe",
        string protocol = "UDP",
        string ports = "7000-8000") =>
        new QosPolicyValue(dscp, app, protocol, ports).Canonical;

    // ---- the value --------------------------------------------------------------------------

    [Fact]
    public void AWellFormedPolicyIsAccepted() => Specification().Validate(Value());

    [Fact]
    public void TheCanonicalFormRoundTrips()
    {
        var parsed = QosPolicyValue.Parse(Value());

        Assert.Equal(46, parsed.Dscp);
        Assert.Equal("VALORANT-Win64-Shipping.exe", parsed.Application);
        Assert.Equal("UDP", parsed.Protocol);
        Assert.Equal("7000-8000", parsed.RemotePorts);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    [InlineData(255)]
    public void ADscpOutsideTheSixBitFieldIsRefused(int dscp) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Specification().Validate(Value(dscp: dscp)));

    [Fact]
    public void APolicyWithNoApplicationIsRefused() =>
        // A policy matching everything would mark the whole machine's traffic, which is not what
        // anybody means by "prioritise my game".
        Assert.Throws<ArgumentOutOfRangeException>(() => Specification().Validate(Value(app: "")));

    [Theory]
    [InlineData("ICMP")]
    [InlineData("udp")]
    [InlineData("")]
    public void AnUnsupportedProtocolIsRefused(string protocol) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Specification().Validate(Value(protocol: protocol)));

    [Theory]
    [InlineData("*")]
    [InlineData("7777")]
    [InlineData("7000-8000")]
    [InlineData("1-65535")]
    public void ValidPortSpecificationsAreAccepted(string ports) => Specification().Validate(Value(ports: ports));

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("8000-7000")]
    [InlineData("7000-")]
    [InlineData("7000-8000-9000")]
    [InlineData("abc")]
    public void InvalidPortSpecificationsAreRefused(string ports) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Specification().Validate(Value(ports: ports)));

    [Fact]
    public void AnApplicationCarryingTheFieldSeparatorIsRefused() =>
        // Otherwise a crafted value could add or replace fields when it is parsed back.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Specification().Validate(Value(app: "game.exe;dscp=0")));

    [Fact]
    public void AValueNotInCanonicalFormIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Specification().Validate("app=game.exe;dscp=46;protocol=UDP;remote=*"));

    // ---- the address ------------------------------------------------------------------------

    [Fact]
    public void ASockTunerPolicyResolvesUnderTheDocumentedGroupPolicyKey()
    {
        var address = Specification().ResolveAddress(Name);

        Assert.Equal($@"{QosPolicySpecification.PolicyRoot}\{Name}", address.RegistryPath);
        Assert.Equal("DSCP Value", address.ValueName);
    }

    [Fact]
    public void APolicySomebodyElseDeployedCannotBeTargeted() =>
        // The whole point of the prefix: a plan must not be able to name — and therefore remove —
        // a policy an administrator pushed.
        Assert.Throws<ArgumentException>(() => Specification().ResolveAddress("Corporate VoIP policy"));

    [Theory]
    [InlineData(QosPolicySpecification.NamePrefix + @"..\..\Run")]
    [InlineData(QosPolicySpecification.NamePrefix + @"a\b")]
    [InlineData(QosPolicySpecification.NamePrefix + "a/b")]
    public void ANameThatCouldTraverseTheRegistryPathIsRefused(string name) =>
        Assert.Throws<ArgumentException>(() => Specification().ResolveAddress(name));

    [Fact]
    public void AnUnreasonablyLongNameIsRefused() =>
        Assert.Throws<ArgumentException>(() =>
            Specification().ResolveAddress(QosPolicySpecification.NamePrefix + new string('a', 200)));

    [Fact]
    public void AnEmptyNameIsRefused() =>
        Assert.Throws<ArgumentException>(() => Specification().ResolveAddress("  "));

    [Fact]
    public void ANameIsDerivedFromTheExecutableAndCarriesThePrefix()
    {
        var name = QosPolicySpecification.NameFor(@"C:\Games\VALORANT-Win64-Shipping.exe");

        Assert.StartsWith(QosPolicySpecification.NamePrefix, name, StringComparison.Ordinal);
        Specification().ResolveAddress(name);
    }

    // ---- the setting's own claims ------------------------------------------------------------

    [Fact]
    public void RemovingThePolicyIsAMeaningfulState() =>
        // Absent is the real "no policy", and it is what a rollback restores.
        Assert.True(Specification().SupportsAbsentValue);

    [Fact]
    public void ApplyingAPolicyDoesNotInterruptTheNetwork() =>
        Assert.False(Services.Diagnosis.RemoteSessionGuard.Disrupts(Specification()));

    [Fact]
    public void TheTradeOffSaysThatAMarkIsARequestRatherThanAReservation() =>
        // "QoS" is sold as a magic word. The one thing this must not do is imply a guarantee.
        Assert.Contains("not a reservation", Specification().TradeOff, StringComparison.Ordinal);

    // ---- store ownership ----------------------------------------------------------------------

    [Fact]
    public async Task TheStoreRefusesAnAddressThatIsNotItsOwn()
    {
        var store = new QosPolicyStore(Specification());
        var foreign = new SettingAddress("irq.affinity", null, "SYSTEM\\Whatever", "DevicePolicy",
            Microsoft.Win32.RegistryValueKind.DWord);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ReadAsync(foreign, CancellationToken.None));
    }

    [Fact]
    public async Task TheStoreRefusesToWriteThroughAnAddressItDidNotResolve()
    {
        var store = new QosPolicyStore(Specification());

        // A hand-built address pointing at a policy name the specification would reject.
        var forged = new SettingAddress(
            QosPolicySpecification.SettingId, "Corporate VoIP policy",
            $@"{QosPolicySpecification.PolicyRoot}\Corporate VoIP policy", "DSCP Value",
            Microsoft.Win32.RegistryValueKind.String);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteAsync(forged, new StoredSettingValue(true, Value()), CancellationToken.None));
    }
}
