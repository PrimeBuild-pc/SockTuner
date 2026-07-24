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

        Assert.Throws<ArgumentOutOfRangeException>(() => definition.Validate(2));
        Assert.Throws<KeyNotFoundException>(() => SettingCatalog.Get("not.allowlisted"));
    }

    [Fact]
    public void NetworkThrottlingIndex_AllowsDocumentedRangeAndDisabledSentinelOnly()
    {
        var definition = SettingCatalog.Get("mmcss.network-throttling-index");

        definition.Validate(1);
        definition.Validate(70);
        definition.Validate(uint.MaxValue);
        Assert.Throws<ArgumentOutOfRangeException>(() => definition.Validate(71));
    }

    [Fact]
    public void SystemResponsiveness_AllowsMultiplesOfTenOnly()
    {
        var definition = SettingCatalog.Get("mmcss.system-responsiveness");

        definition.Validate(10);
        definition.Validate(100);
        Assert.Throws<ArgumentOutOfRangeException>(() => definition.Validate(15));
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
