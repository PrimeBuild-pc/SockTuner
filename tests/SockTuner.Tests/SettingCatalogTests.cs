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
    public void EveryWritableSettingCitesItsEvidence()
    {
        // An EvidenceLevel on its own is an assertion. Each entry must say which documentation
        // or which Windows component backs it — including entries whose honest answer is that
        // nothing has been verified yet.
        foreach (var definition in SettingCatalog.All)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(definition.EvidenceNote),
                $"{definition.Id} carries an evidence level with no supporting note.");
        }
    }

    [Fact]
    public void UnverifiedSettingsAreNotPresentedAsDocumented()
    {
        // TCPNoDelay and TcpDelAckTicks have no confirmed consuming binary and no Microsoft
        // documentation, so neither may claim the Documented level.
        foreach (var id in new[] { "tcp.interface.no-delay", "tcp.interface.delayed-ack-ticks" })
        {
            var definition = SettingCatalog.Get(id);
            Assert.Equal(EvidenceLevel.Experimental, definition.Evidence);
            Assert.Contains("Unverified", definition.EvidenceNote, StringComparison.Ordinal);
        }
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
