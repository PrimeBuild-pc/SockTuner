using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

public sealed class SettingCatalogTests
{
    [Fact]
    public void AdapterAddress_RequiresAndNormalizesGuid()
    {
        var definition = SettingCatalog.Get("tcp.interface.no-delay");
        var guid = Guid.Parse("12345678-1234-1234-1234-123456789abc");

        var address = definition.ResolveAddress(guid.ToString());

        Assert.Equal("{12345678-1234-1234-1234-123456789ABC}", address.TargetId);
        Assert.EndsWith(address.TargetId, address.RegistryPath, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogRejectsUnknownOrOutOfRangeValues()
    {
        var definition = SettingCatalog.Get("tcp.interface.no-delay");

        Assert.Throws<ArgumentOutOfRangeException>(() => definition.Validate("2"));
        Assert.Throws<KeyNotFoundException>(() => SettingCatalog.Get("not.allowlisted"));
    }

    [Fact]
    public void NetworkThrottlingIndex_AllowsDocumentedRangeAndDisabledSentinelOnly()
    {
        var definition = SettingCatalog.Get("mmcss.network-throttling-index");

        definition.Validate("1");
        definition.Validate("70");
        definition.Validate("4294967295");
        Assert.Throws<ArgumentOutOfRangeException>(() => definition.Validate("71"));
    }

    [Fact]
    public void SystemResponsiveness_AllowsMultiplesOfTenOnly()
    {
        var definition = SettingCatalog.Get("mmcss.system-responsiveness");

        definition.Validate("10");
        definition.Validate("100");
        Assert.Throws<ArgumentOutOfRangeException>(() => definition.Validate("15"));
    }

    [Theory]
    [InlineData("010")]      // leading zero round-trips to "10"
    [InlineData(" 10")]      // leading space
    [InlineData("10 ")]      // trailing space
    [InlineData("+10")]      // explicit sign
    [InlineData("1,0")]      // group separator
    [InlineData("0x10")]     // hexadecimal
    [InlineData("ten")]
    [InlineData("")]
    public void Validate_RejectsNonCanonicalNumericText(string value)
    {
        var definition = SettingCatalog.Get("mmcss.system-responsiveness");

        Assert.Throws<ArgumentOutOfRangeException>(() => definition.Validate(value));
    }

    [Fact]
    public void TryParseCanonical_AcceptsOnlyExactRoundTrippableText()
    {
        Assert.True(SettingDefinition.TryParseCanonical("0", out var zero));
        Assert.Equal(0u, zero);
        Assert.True(SettingDefinition.TryParseCanonical("4294967295", out var maximum));
        Assert.Equal(uint.MaxValue, maximum);
        Assert.False(SettingDefinition.TryParseCanonical("4294967296", out _));
        Assert.False(SettingDefinition.TryParseCanonical("-1", out _));
        Assert.False(SettingDefinition.TryParseCanonical(null, out _));
    }

    [Fact]
    public void AddressValidationRejectsArbitraryRegistryPath()
    {
        var definition = SettingCatalog.Get("mmcss.system-responsiveness");
        var valid = definition.ResolveAddress(null);
        var forged = valid with { RegistryPath = @"SOFTWARE\Other" };

        Assert.Throws<InvalidOperationException>(() => SettingCatalog.ValidateAddress(forged));
    }

    [Fact]
    public void EverySettingCitesItsEvidence()
    {
        // An evidence level on its own is an assertion. Each entry must say which documentation or
        // which Windows component backs it — including entries whose honest answer is "nothing yet".
        foreach (var definition in SettingCatalog.All)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(definition.EvidenceNote),
                $"{definition.Id} carries an evidence level with no supporting note.");
        }
    }

    [Theory]
    [InlineData("tcp.interface.no-delay")]
    [InlineData("tcp.interface.delayed-ack-ticks")]
    public void UnverifiedSettingsAreNotPresentedAsDocumented(string id)
    {
        // Neither has a confirmed consuming binary or Microsoft documentation, so neither may
        // claim the Documented level while its note says otherwise.
        var definition = SettingCatalog.Get(id);

        Assert.Equal(EvidenceLevel.Experimental, definition.Evidence);
        Assert.StartsWith("Unverified", definition.EvidenceNote, StringComparison.Ordinal);
    }

    [Fact]
    public void InterfaceMetric_RestoresTheAutomaticMetricByRemovingTheValueRatherThanWritingOne()
    {
        // Absent is the real state here, the same way DHCP is for resolvers: Windows derives the
        // metric from link speed when nothing overrides it. Writing a number back would not be a
        // rollback, it would be a different setting that happened to match.
        var definition = SettingCatalog.Get("tcp.interface.metric");

        Assert.True(definition.SupportsAbsentValue);
        Assert.Equal(SettingScope.AdapterInterface, definition.Scope);
        Assert.Equal(RemoteSessionGuard.AdapterRestart, definition.RestartRequirement);
        definition.Validate("1");
        definition.Validate("9999");
        Assert.Throws<ArgumentOutOfRangeException>(() => definition.Validate("0"));
        Assert.Throws<ArgumentOutOfRangeException>(() => definition.Validate("10000"));
    }

    [Fact]
    public void NoWritableSettingBlanketDisablesIpv6BindingsOrHiddenAdapters()
    {
        // A Step 8 exit criterion, held by a test rather than by nobody having added one yet. The
        // three value names below are the ones every "network reset" guide reaches for, and each one
        // turns a targeted tool into a blunt instrument whose damage cannot be rolled back exactly.
        string[] forbidden = ["DisabledComponents", "EnableIPv6", "ArpRetryCount"];

        foreach (var definition in SettingCatalog.All)
        {
            Assert.DoesNotContain(definition.ValueName, forbidden, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheWinsockCatalogIsShownAndDeclinedRatherThanReset()
    {
        // The other Step 8 exit criterion: a broad reset is not a rollback. netsh winsock reset
        // rebuilds from defaults, so what it replaced cannot be restored — it must stay declined
        // on the record instead of shipping as a repair button.
        var declined = InertSettingCatalog.All.Single(setting =>
            setting.Name.Contains("Winsock catalog reset", StringComparison.Ordinal));

        Assert.Equal(DiagnosticConfidence.High, declined.Confidence);
        Assert.Contains("rolled back", declined.Reality, StringComparison.Ordinal);
        Assert.DoesNotContain(SettingCatalog.All, definition =>
            definition.RegistryPath.Contains("WinSock", StringComparison.OrdinalIgnoreCase));
    }
}
