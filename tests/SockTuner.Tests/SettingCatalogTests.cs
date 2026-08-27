using SockTuner.Models;
using SockTuner.Services;

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
}
