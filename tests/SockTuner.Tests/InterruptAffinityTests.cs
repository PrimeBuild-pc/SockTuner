using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Collection;

namespace SockTuner.Tests;

public sealed class InterruptAffinityTests
{
    private const string Device = @"PCI\VEN_8086&DEV_125C\3&11583659&0&C8";
    private static readonly IReadOnlySet<string> Present =
        new HashSet<string>([Device], StringComparer.OrdinalIgnoreCase);

    private static InterruptAffinitySpecification Specification(int processors = 16) =>
        new(processors, Present);

    // ---- mask arithmetic ------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { 0 }, 1UL)]
    [InlineData(new[] { 1 }, 2UL)]
    [InlineData(new[] { 0, 1 }, 3UL)]
    [InlineData(new[] { 4, 5 }, 48UL)]
    [InlineData(new int[0], 0UL)]
    public void CoresConvertToTheProcessorMask(int[] cores, ulong expected) =>
        Assert.Equal(expected, InterruptAffinityDevice.ToMask(cores));

    [Fact]
    public void TheMaskConvertsBackToTheSameCores() =>
        Assert.Equal([2, 3, 9], InterruptAffinityDevice.ToCores(InterruptAffinityDevice.ToMask([2, 3, 9])));

    [Fact]
    public void AProcessorOutsideTheRepresentableRangeIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => InterruptAffinityDevice.ToMask([64]));

    [Fact]
    public void TheMaskIsStoredLittleEndianAsWindowsWritesIt()
    {
        // CPU 8 is the first bit of the second byte.
        Assert.Equal([0x00, 0x01], InterruptAffinityInventory.ToBytes(InterruptAffinityDevice.ToMask([8])));
        Assert.Equal([0x03], InterruptAffinityInventory.ToBytes(InterruptAffinityDevice.ToMask([0, 1])));
    }

    [Fact]
    public void ReadingBackTheStoredBytesGivesTheSameMask()
    {
        var mask = InterruptAffinityDevice.ToMask([0, 7, 15]);

        Assert.Equal(mask, InterruptAffinityInventory.ToMask(InterruptAffinityInventory.ToBytes(mask)));
    }

    // ---- validation -----------------------------------------------------------------------

    [Fact]
    public void PinningToProcessorsThisMachineHasIsAccepted() =>
        Specification().Validate(InterruptAffinitySpecification.Canonical(
            InterruptPolicy.SpecifiedProcessors, InterruptPriority.High, InterruptAffinityDevice.ToMask([2, 3])));

    [Fact]
    public void AProcessorTheMachineDoesNotHaveIsRefused()
    {
        // The failure this prevents is the bad one: a mask naming only absent processors leaves the
        // device with no eligible core at all.
        var value = InterruptAffinitySpecification.Canonical(
            InterruptPolicy.SpecifiedProcessors, InterruptPriority.Undefined, InterruptAffinityDevice.ToMask([20]));

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Specification(16).Validate(value));
        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SpecifiedProcessorsWithAnEmptyMaskIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Specification().Validate(
            InterruptAffinitySpecification.Canonical(
                InterruptPolicy.SpecifiedProcessors, InterruptPriority.Undefined, 0)));

    [Fact]
    public void AMaskWithAPolicyThatIgnoresItIsRefused()
    {
        // Windows only consults the mask under SpecifiedProcessors. Accepting one alongside another
        // policy would store a value that silently does nothing.
        Assert.Throws<ArgumentOutOfRangeException>(() => Specification().Validate(
            InterruptAffinitySpecification.Canonical(
                InterruptPolicy.AllCloseProcessors, InterruptPriority.Undefined, InterruptAffinityDevice.ToMask([1]))));
    }

    [Fact]
    public void WindowsDefaultCannotBeWrittenAsAPolicyValue()
    {
        // Leaving DevicePolicy = 0 behind still reads as an override later; the default is expressed
        // by removing the key.
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Specification().Validate(
            InterruptAffinitySpecification.Canonical(InterruptPolicy.MachineDefault, InterruptPriority.Undefined, 0)));

        Assert.Contains("removing the override", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("4:3")]
    [InlineData("4:3:0:0")]
    [InlineData("nonsense")]
    [InlineData("9:3:1")]
    [InlineData("4:3:0X1")]
    [InlineData("4:3:01")]
    public void AValueThatIsNotACanonicalTripleIsRefused(string value) =>
        Assert.ThrowsAny<ArgumentException>(() => Specification().Validate(value));

    [Fact]
    public void TheTripleRoundTripsThroughItsCanonicalForm()
    {
        var value = InterruptAffinitySpecification.Canonical(
            InterruptPolicy.SpecifiedProcessors, InterruptPriority.High, InterruptAffinityDevice.ToMask([3, 4]));

        var (policy, priority, mask) = InterruptAffinitySpecification.Parse(value);

        Assert.Equal(InterruptPolicy.SpecifiedProcessors, policy);
        Assert.Equal(InterruptPriority.High, priority);
        Assert.Equal([3, 4], InterruptAffinityDevice.ToCores(mask));
    }

    // ---- addressing -----------------------------------------------------------------------

    [Fact]
    public void TheAddressIsDerivedFromTheDeviceInstanceId()
    {
        var address = Specification().ResolveAddress(Device);

        Assert.Contains(Device, address.RegistryPath, StringComparison.Ordinal);
        Assert.EndsWith(@"Interrupt Management\Affinity Policy", address.RegistryPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeviceThatIsNotPresentIsRefused()
    {
        // The instance ID becomes a registry path, so a plan naming an unknown device must not be
        // able to steer this setting at an arbitrary key.
        Assert.Throws<KeyNotFoundException>(() =>
            Specification().ResolveAddress(@"PCI\VEN_DEAD&DEV_BEEF\0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void AMissingInstanceIdIsRefused(string? instanceId) =>
        Assert.Throws<ArgumentException>(() => Specification().ResolveAddress(instanceId));

    // ---- store ---------------------------------------------------------------------------

    [Fact]
    public async Task TheStoreRefusesAnAddressThatIsNotItsOwn()
    {
        var store = new InterruptAffinityStore(Specification());
        var foreign = new SettingAddress(
            "mmcss.system-responsiveness", null, "SOFTWARE\\X", "Y", Microsoft.Win32.RegistryValueKind.DWord);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(foreign, CancellationToken.None));
    }

    [Fact]
    public async Task TheStoreRefusesAnAddressWhosePathWasTamperedWith()
    {
        // Same setting id and a present device, but a path the specification would never produce.
        var store = new InterruptAffinityStore(Specification());
        var forged = Specification().ResolveAddress(Device) with { RegistryPath = @"SYSTEM\CurrentControlSet\Services\Tcpip" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(
            forged, new StoredSettingValue(true, "4:0:1"), CancellationToken.None));
    }

    // ---- presentation ---------------------------------------------------------------------

    [Fact]
    public void ADeviceWithNoOverrideReportsTheWindowsDefault()
    {
        var device = new InterruptAffinityDevice(
            Device, "Intel I226-V", "Net", InterruptPolicy.MachineDefault, InterruptPriority.Undefined, null, true, true);

        Assert.False(device.HasOverride);
        Assert.Equal("Windows default", device.StateDisplay);
        Assert.Equal("—", device.CoresDisplay);
    }

    [Fact]
    public void APinnedDeviceNamesItsProcessors()
    {
        var device = new InterruptAffinityDevice(
            Device, "Intel I226-V", "Net", InterruptPolicy.SpecifiedProcessors, InterruptPriority.High,
            InterruptAffinityDevice.ToMask([6, 7]), true, true);

        Assert.True(device.HasOverride);
        Assert.Equal("6, 7", device.CoresDisplay);
        Assert.Contains("CPU 6, 7", device.StateDisplay, StringComparison.Ordinal);
    }
}
