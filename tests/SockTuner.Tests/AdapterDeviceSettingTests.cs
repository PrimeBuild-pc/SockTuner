using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

/// <summary>
/// The two device-level writes, checked entirely against their specifications: no adapter is
/// enabled, disabled or repowered by this suite.
/// </summary>
public sealed class AdapterDeviceSettingTests
{
    private static readonly Guid Present = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Absent = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private static AdapterStateSpecification State() => new(new HashSet<Guid> { Present });

    private static AdapterPowerSavingSpecification Power() =>
        new(new Dictionary<Guid, string> { [Present] = "0007" });

    // ---- adapter state --------------------------------------------------------------------

    [Theory]
    [InlineData(AdapterStateSpecification.Enabled)]
    [InlineData(AdapterStateSpecification.Disabled)]
    public void TheTwoAdapterStatesAreAccepted(string value) => State().Validate(value);

    [Theory]
    [InlineData("enabled")]
    [InlineData("On")]
    [InlineData("1")]
    [InlineData("")]
    public void AnythingElseIsNotAnAdapterState(string value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => State().Validate(value));

    [Fact]
    public void AnAdapterThisMachineDoesNotHaveCannotBeTargeted() =>
        Assert.Throws<KeyNotFoundException>(() => State().ResolveAddress(Absent.ToString()));

    [Fact]
    public void AnAdapterTargetMustBeAGuid() =>
        Assert.Throws<ArgumentException>(() => State().ResolveAddress("MSFT_NetAdapter"));

    [Fact]
    public void TheResolvedStateAddressNamesTheProviderRatherThanARegistryPath()
    {
        var address = State().ResolveAddress(Present.ToString());

        Assert.Equal(AdapterStateSpecification.SettingId, address.SettingId);
        Assert.Equal("MSFT_NetAdapter", address.RegistryPath);
        Assert.Equal(Present.ToString("B").ToUpperInvariant(), address.TargetId);
    }

    [Fact]
    public void AnAdapterIsNeverAbsent() => Assert.False(State().SupportsAbsentValue);

    [Fact]
    public void DisablingAnAdapterIsClassifiedAsInterruptingTheNetwork()
    {
        // The guard's table is exhaustive by design, so a new requirement has to be classified
        // rather than silently defaulting.
        Assert.True(RemoteSessionGuard.RestartRequirements[AdapterStateSpecification.DisableRestart]);
        Assert.True(RemoteSessionGuard.Disrupts(State()));
    }

    [Fact]
    public void DisablingAnAdapterIsHighRisk() => Assert.Equal(ChangeRisk.High, State().Risk);

    // ---- power management -----------------------------------------------------------------

    [Theory]
    [InlineData("0")]
    [InlineData("8")]
    [InlineData("16")]
    [InlineData("24")]
    [InlineData("256")]
    [InlineData("280")]
    public void DocumentedPowerManagementBitsAreAccepted(string value) => Power().Validate(value);

    [Theory]
    [InlineData("1")]
    [InlineData("32")]
    [InlineData("4294967295")]
    public void BitsOutsideTheDocumentedMaskAreRefused(string value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Power().Validate(value));

    [Theory]
    [InlineData("-8")]
    [InlineData("0x18")]
    [InlineData("twenty-four")]
    public void APowerValueMustBeAnUnsignedDecimalDword(string value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Power().Validate(value));

    [Fact]
    public void ThePowerAddressIsBuiltFromTheDiscoveredClassKeyNotFromThePlan()
    {
        var address = Power().ResolveAddress(Present.ToString());

        Assert.Equal($@"{AdapterPowerSavingSpecification.NetClassKey}\0007", address.RegistryPath);
        Assert.Equal("PnPCapabilities", address.ValueName);
    }

    [Fact]
    public void AnAdapterWithNoClassKeyCannotBeTargeted() =>
        Assert.Throws<KeyNotFoundException>(() => Power().ResolveAddress(Absent.ToString()));

    [Fact]
    public void TheWindowsDefaultForPowerManagementIsNoValueAtAll() =>
        Assert.True(Power().SupportsAbsentValue);

    [Fact]
    public void PowerManagementOffClearsBothCheckboxes() =>
        Assert.Equal(24u, AdapterPowerSavingSpecification.PowerManagementOff);

    // ---- store ownership ------------------------------------------------------------------

    [Fact]
    public async Task ThePowerStoreRefusesAnAddressThatIsNotItsOwn()
    {
        var store = new AdapterPowerSavingStore(Power());
        var foreign = new SettingAddress("irq.affinity", null, "SYSTEM\\Whatever", "DevicePolicy",
            Microsoft.Win32.RegistryValueKind.DWord);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ReadAsync(foreign, CancellationToken.None));
    }

    [Fact]
    public async Task TheStateStoreRefusesAnAddressThatIsNotItsOwn()
    {
        var store = new AdapterStateStore();
        var foreign = new SettingAddress("nic.*EEE", null, "MSFT_NetAdapter", "State",
            Microsoft.Win32.RegistryValueKind.String);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ReadAsync(foreign, CancellationToken.None));
    }

    [Theory]
    [InlineData("024")]
    [InlineData("0024")]
    [InlineData("+24")]
    public void ANonCanonicalPowerValueIsRefused(string value) =>
        // The store writes a DWORD and reads back the shortest form, so "024" would pass validation
        // and then fail read-back verification for no reason a user could act on.
        Assert.Throws<ArgumentOutOfRangeException>(() => Power().Validate(value));

    [Fact]
    public async Task AnAdapterStateCannotBeRemoved()
    {
        var store = new AdapterStateStore();
        var address = State().ResolveAddress(Present.ToString());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.WriteAsync(address, StoredSettingValue.Missing, CancellationToken.None));
    }
}
