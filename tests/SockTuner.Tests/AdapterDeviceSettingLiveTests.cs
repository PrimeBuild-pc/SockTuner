using System.Net.NetworkInformation;
using SockTuner.Services;

namespace SockTuner.Tests;

/// <summary>
/// Read-only checks that the two device settings resolve against the adapters this machine really
/// has. Nothing here enables, disables or repowers anything: every test stops at
/// <see cref="ISettingSpecification.ResolveAddress"/> and at reading a value back.
/// </summary>
public sealed class AdapterDeviceSettingLiveTests
{
    private static Guid FirstRealAdapter() => NetworkInterface
        .GetAllNetworkInterfaces()
        .Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Select(item => Guid.TryParse(item.Id, out var id) ? id : Guid.Empty)
        .FirstOrDefault(id => id != Guid.Empty);

    [LiveWindowsFact]
    public void ThePresentAdapterSetIsNotEmptyAndContainsARealInterface()
    {
        var present = AdapterStateSpecification.PresentAdapters();

        Assert.NotEmpty(present);
        Assert.Contains(FirstRealAdapter(), present);
    }

    [LiveWindowsFact]
    public void AnAdapterStateResolvesForAnAdapterThisMachineHas()
    {
        var specification = new AdapterStateSpecification(AdapterStateSpecification.PresentAdapters());

        var address = specification.ResolveAddress(FirstRealAdapter().ToString());

        Assert.Equal(AdapterStateSpecification.SettingId, address.SettingId);
        Assert.Equal("MSFT_NetAdapter", address.RegistryPath);
    }

    [LiveWindowsFact]
    public void EveryNetworkClassKeyFoundMapsBackToAGuid()
    {
        var keys = AdapterPowerSavingSpecification.ReadAdapterKeys();

        // A machine with any NIC at all has at least one instance key under the network class.
        Assert.NotEmpty(keys);
        Assert.All(keys, entry =>
        {
            Assert.NotEqual(Guid.Empty, entry.Key);
            Assert.Equal(4, entry.Value.Length);
            Assert.True(entry.Value.All(char.IsAsciiDigit));
        });
    }

    [LiveWindowsFact]
    public async Task ThePowerManagementValueCanBeReadBackForARealAdapter()
    {
        var keys = AdapterPowerSavingSpecification.ReadAdapterKeys();
        var specification = new AdapterPowerSavingSpecification(keys);
        var store = new AdapterPowerSavingStore(specification);
        var target = keys.Keys.First();

        var address = specification.ResolveAddress(target.ToString());
        var value = await store.ReadAsync(address, CancellationToken.None);

        Assert.StartsWith(AdapterPowerSavingSpecification.NetClassKey, address.RegistryPath, StringComparison.Ordinal);

        // Absent is the normal state on a machine nobody has changed; a present value must be one
        // the specification would itself accept, or the read and the write disagree.
        if (value.Exists)
        {
            specification.Validate(value.Value);
        }
    }

    [LiveWindowsFact]
    public void AnAdapterThatIsNotPresentIsRefusedByBothDeviceSettings()
    {
        var missing = Guid.NewGuid().ToString();

        Assert.Throws<KeyNotFoundException>(() =>
            new AdapterStateSpecification(AdapterStateSpecification.PresentAdapters()).ResolveAddress(missing));
        Assert.Throws<KeyNotFoundException>(() =>
            new AdapterPowerSavingSpecification(AdapterPowerSavingSpecification.ReadAdapterKeys())
                .ResolveAddress(missing));
    }
}
