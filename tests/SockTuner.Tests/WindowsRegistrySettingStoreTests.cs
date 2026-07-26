using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class WindowsRegistrySettingStoreTests
{
    [Fact]
    public async Task ReadOnlyStore_RejectsEveryWriteBeforeOpeningWritableRegistry()
    {
        var store = WindowsRegistrySettingStore.CreateReadOnly();
        var address = SettingCatalog.Get("mmcss.system-responsiveness").ResolveAddress(null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.WriteAsync(address, new StoredSettingValue(true, 20), CancellationToken.None));

        Assert.Contains("read-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsolatedVmGate_AllowsOnlySettingsThatPassedLiveRollbackValidation()
    {
        WindowsRegistrySettingStore.EnsureValidatedForIsolatedVm(
            SettingCatalog.Get("mmcss.system-responsiveness").ResolveAddress(null));
        WindowsRegistrySettingStore.EnsureValidatedForIsolatedVm(
            SettingCatalog.Get("mmcss.network-throttling-index").ResolveAddress(null));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WindowsRegistrySettingStore.EnsureValidatedForIsolatedVm(
                SettingCatalog.Get("tcp.interface.no-delay").ResolveAddress(Guid.NewGuid().ToString())));

        Assert.Contains("has not passed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
