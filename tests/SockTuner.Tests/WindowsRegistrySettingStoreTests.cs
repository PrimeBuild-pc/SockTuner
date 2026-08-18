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
            store.WriteAsync(address, new StoredSettingValue(true, "20"), CancellationToken.None));

        Assert.Contains("read-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("mmcss.system-responsiveness", null)]
    [InlineData("mmcss.network-throttling-index", null)]
    [InlineData("tcp.interface.no-delay", "12345678-1234-1234-1234-123456789abc")]
    [InlineData("tcp.interface.ack-frequency", "12345678-1234-1234-1234-123456789abc")]
    [InlineData("tcp.interface.delayed-ack-ticks", "12345678-1234-1234-1234-123456789abc")]
    public void WriteGate_AcceptsEveryEnabledCatalogSetting(string settingId, string? targetId)
    {
        WindowsRegistrySettingStore.EnsureWritable(SettingCatalog.Get(settingId).ResolveAddress(targetId));
    }

    [Fact]
    public void WriteGate_RejectsAnAddressThatDoesNotMatchTheCatalog()
    {
        var forged = SettingCatalog.Get("mmcss.system-responsiveness").ResolveAddress(null)
            with
        { RegistryPath = @"SOFTWARE\Elsewhere" };

        Assert.Throws<InvalidOperationException>(() => WindowsRegistrySettingStore.EnsureWritable(forged));
    }

    [Fact]
    public void ExperimentalTcpSettings_RequireExplicitConfirmationWhileDocumentedOnesDoNot()
    {
        Assert.True(Change("tcp.interface.no-delay").RequiresExplicitConfirmation);
        Assert.True(Change("tcp.interface.ack-frequency").RequiresExplicitConfirmation);
        Assert.True(Change("tcp.interface.delayed-ack-ticks").RequiresExplicitConfirmation);
        Assert.False(Change("mmcss.system-responsiveness").RequiresExplicitConfirmation);
        Assert.False(Change("mmcss.network-throttling-index").RequiresExplicitConfirmation);
    }

    private static PlannedChange Change(string settingId)
    {
        var definition = SettingCatalog.Get(settingId);
        var targetId = definition.Scope == SettingScope.AdapterInterface
            ? "12345678-1234-1234-1234-123456789abc"
            : null;
        return new PlannedChange(
            definition,
            definition.ResolveAddress(targetId),
            StoredSettingValue.Missing,
            new StoredSettingValue(true, definition.Minimum.ToString()));
    }
}
