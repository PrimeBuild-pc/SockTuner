using System.Management;
using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

/// <summary>
/// The global TCP and offload write surface. The provider is the allowlist here, so these check
/// that nothing is accepted which the provider did not advertise — against fake capabilities, with
/// no CIM call and no host state touched.
/// </summary>
public sealed class CimGlobalSettingTests
{
    [Fact]
    public void EnumeratedPropertyAcceptsOnlyTheValuesTheProviderAdvertised()
    {
        var capability = Enumerated("AutoTuningLevelLocal", "3", "0", "1", "2", "3", "4");

        capability.Validate("0");
        capability.Validate("4");
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate("5"));
    }

    [Fact]
    public void NumericPropertyIsBoundedByItsDocumentedRange()
    {
        var capability = Numeric("InitialRto", "3000", 300, 3000);

        capability.Validate("300");
        capability.Validate("3000");
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate("299"));
        Assert.Throws<ArgumentOutOfRangeException>(() => capability.Validate("3001"));
    }

    [Theory]
    [InlineData("0300")]
    [InlineData(" 300")]
    [InlineData("+300")]
    [InlineData("three hundred")]
    public void NumericPropertyRejectsNonCanonicalText(string value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Numeric("InitialRto", "3000", 300, 3000).Validate(value));

    [Fact]
    public void APropertyWithNeitherAnEnumerationNorARangeIsNotWritable()
    {
        var capability = Enumerated("Mystery", "1") with { Choices = [], Minimum = null, Maximum = null };

        Assert.Throws<InvalidOperationException>(() => capability.Validate("1"));
        Assert.Contains("Not writable", capability.ConstraintDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void AddressCarriesTheClassTheInstanceAndTheProperty()
    {
        var specification = new CimGlobalSettingSpecification(Enumerated("Timestamps", "0", "0", "1"));

        var address = specification.ResolveAddress("InternetCustom");

        Assert.Equal("cim.MSFT_NetTCPSetting.Timestamps", address.SettingId);
        Assert.Equal("InternetCustom", address.TargetId);
        Assert.Equal("MSFT_NetTCPSetting", address.RegistryPath);
        Assert.Equal("Timestamps", address.ValueName);
    }

    [Fact]
    public void AddressForTheWrongInstanceIsRefused()
    {
        var specification = new CimGlobalSettingSpecification(Enumerated("Timestamps", "0", "0", "1"));

        Assert.Throws<ArgumentException>(() => specification.ResolveAddress("Datacenter"));
    }

    [Fact]
    public void SingletonClassesResolveWithoutAnInstanceKey()
    {
        var capability = Enumerated("ReceiveSegmentCoalescing", "1", "0", "1") with
        {
            ClassName = CimGlobalPropertyCatalog.OffloadGlobalClass,
            InstanceKey = null
        };

        var address = new CimGlobalSettingSpecification(capability).ResolveAddress(null);

        Assert.Null(address.TargetId);
        Assert.Equal("System", capability.InstanceDisplay);
    }

    [Fact]
    public void APropertyThisBuildDoesNotExposeCannotBeResolved()
    {
        var available = new[] { Enumerated("Timestamps", "0", "0", "1") };

        Assert.Throws<KeyNotFoundException>(() => CimGlobalSettingSpecification.Resolve(
            "cim.MSFT_NetTCPSetting.CongestionProvider", "InternetCustom", available));
    }

    [Fact]
    public void ResolveRejectsAMalformedSettingId() =>
        Assert.Throws<ArgumentException>(() => CimGlobalSettingSpecification.Resolve("cim.OnlyOnePart", null, []));

    [Fact]
    public async Task GlobalPropertiesFlowThroughTheTransactionEngineLikeAnyOtherSetting()
    {
        var capability = Enumerated("AutoTuningLevelLocal", "3", "0", "1", "2", "3", "4");
        var transactions = new SettingTransactionService(SettingSpecifications.From([], [capability]));
        var store = new MemoryStore();
        var address = new CimGlobalSettingSpecification(capability).ResolveAddress("InternetCustom");
        store.Values[address] = new StoredSettingValue(true, "3");

        var plan = await transactions.PrepareAsync(
            [new ChangeRequest(capability.SettingId, "InternetCustom", "2")], store, CancellationToken.None);
        var result = await transactions.ApplyAsync(plan, store, CancellationToken.None);
        var rollbackErrors = await transactions.RollbackAsync(result.Snapshot, store, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(rollbackErrors);
        Assert.Equal(new StoredSettingValue(true, "3"), store.Values[address]);
    }

    [Fact]
    public async Task AValueTheProviderDoesNotAdvertiseNeverReachesAPlan()
    {
        var capability = Enumerated("AutoTuningLevelLocal", "3", "0", "3");
        var transactions = new SettingTransactionService(SettingSpecifications.From([], [capability]));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => transactions.PrepareAsync(
            [new ChangeRequest(capability.SettingId, "InternetCustom", "4")], new MemoryStore(), CancellationToken.None));
    }

    [Fact]
    public async Task GlobalPropertiesCannotBeRemoved()
    {
        var capability = Enumerated("AutoTuningLevelLocal", "3", "0", "3");
        var transactions = new SettingTransactionService(SettingSpecifications.From([], [capability]));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => transactions.PrepareAsync(
            [new ChangeRequest(capability.SettingId, "InternetCustom", null)], new MemoryStore(), CancellationToken.None));

        Assert.Contains("cannot be removed", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CimType.UInt8, typeof(byte))]
    [InlineData(CimType.UInt16, typeof(ushort))]
    [InlineData(CimType.UInt32, typeof(uint))]
    public void WrittenValuesMatchTheTypeTheProviderDeclared(CimType type, Type expected) =>
        Assert.IsType(expected, CimGlobalSettingStore.Convert(type, "Property", "3"));

    [Fact]
    public void AStringPropertyIsRefusedRatherThanCoerced() =>
        Assert.Throws<InvalidOperationException>(() => CimGlobalSettingStore.Convert(CimType.String, "Property", "3"));

    [Fact]
    public void EveryWritableGlobalPropertyDeclaresItsCostAndItsRange()
    {
        Assert.All(CimGlobalPropertyCatalog.All, property =>
        {
            Assert.NotEmpty(property.TradeOff);
            Assert.NotEmpty(property.RestartRequirement);
            // A numeric property needs both bounds or neither; one alone is not a range.
            Assert.Equal(property.Minimum.HasValue, property.Maximum.HasValue);
            Assert.True(property.Minimum is null || property.Minimum < property.Maximum);
        });
    }

    [Fact]
    public void TheEffectivePropertyNamesTheWinningSourceRatherThanTheWinningValue()
    {
        // Read from the live class: ValueMap {Local, GroupPolicy}, two values, against five for the
        // level itself. Treating it as a second copy of the level would flag every write on a
        // machine with no policy at all, because the selector reads Local — which is success.
        var source = CimGlobalPropertyCatalog.PolicySources["AutoTuningLevelLocal"];

        Assert.Equal("AutoTuningLevelEffective", source.SelectorProperty);
        Assert.Equal("AutoTuningLevelGroupPolicy", source.PolicyValueProperty);
        Assert.Equal("1", PolicySource.GroupPolicyWins);
    }

    [Fact]
    public void NothingWritableNamesASettingDocumentedAsInert()
    {
        Assert.All(SettingCatalog.All, definition =>
            Assert.False(InertSettingCatalog.IsInert(definition.ValueName), definition.ValueName));
        Assert.All(CimGlobalPropertyCatalog.All, property =>
            Assert.False(InertSettingCatalog.IsInert(property.Property), property.Property));
    }

    [Fact]
    public void EveryInertEntryExplainsTheClaimAndTheReality() =>
        Assert.All(InertSettingCatalog.All, item =>
        {
            Assert.NotEmpty(item.Claim);
            Assert.NotEmpty(item.Reality);
            Assert.NotEmpty(item.Location);
        });

    [Fact]
    public void TheCatalogIsTheWritableAllowlist()
    {
        // The two were maintained separately while the first entries were unlocked, which let a new
        // catalog entry be planned and then refused at write time.
        Assert.Equal(
            SettingCatalog.All.Where(definition => definition.Evidence != EvidenceLevel.Blocked)
                .Select(definition => definition.Id).Order(),
            WindowsRegistrySettingStore.WritableSettingIds.Order());
        Assert.Contains("tcp.interface.mtu", WindowsRegistrySettingStore.WritableSettingIds);
    }

    private static GlobalSettingCapability Enumerated(string property, string current, params string[] values) => new(
        CimGlobalPropertyCatalog.TcpSettingClass, "InternetCustom", property, property, "TCP",
        current, values.Select(value => new CapabilityChoice(value, value)).ToArray(), null, null,
        EvidenceLevel.Documented, ChangeRisk.Medium, "None", "Test trade-off.");

    private static GlobalSettingCapability Numeric(string property, string current, long minimum, long maximum) =>
        Enumerated(property, current) with { Minimum = minimum, Maximum = maximum };

    private sealed class MemoryStore : ISettingStore
    {
        public Dictionary<SettingAddress, StoredSettingValue> Values { get; } = [];

        public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken) =>
            Task.FromResult(Values.GetValueOrDefault(address, StoredSettingValue.Missing));

        public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken)
        {
            if (value.Exists)
            {
                Values[address] = value;
            }
            else
            {
                Values.Remove(address);
            }

            return Task.CompletedTask;
        }
    }
}

/// <summary>Read-only live check of the CIM global surface; skipped unless explicitly enabled.</summary>
public sealed class WindowsGlobalSettingInventoryLiveTests
{
    [LiveWindowsFact]
    public void Read_ReportsTheProvidersOwnConstraints()
    {
        var result = WindowsGlobalSettingInventory.Read();

        Assert.Null(result.Error);
        Assert.NotEmpty(result.Capabilities);

        // The whole design rests on the provider advertising its accepted values: an enumerated
        // property with no choices would mean falling back to a table SockTuner carries, which is
        // exactly what this avoids.
        var autoTuning = result.Capabilities.Where(item => item.Property == "AutoTuningLevelLocal").ToArray();
        Assert.NotEmpty(autoTuning);
        Assert.All(autoTuning, item => Assert.NotEmpty(item.Choices));
        Assert.All(result.Capabilities, item => Assert.True(item.IsEnumerated || item.IsNumericRange));
    }

    [LiveWindowsFact]
    public void EveryCurrentValueFitsTheConstraintSockTunerDeclaresForIt()
    {
        // A declared range narrower than what Windows ships with is not a harmless mistake: the
        // transaction engine validates the *current* value before planning, so the property becomes
        // unchangeable. This caught DynamicPortRangeStartPort, which ships at 1024 on every template.
        Assert.All(WindowsGlobalSettingInventory.Read().Capabilities, capability =>
            capability.Validate(capability.CurrentValue));
    }
}
