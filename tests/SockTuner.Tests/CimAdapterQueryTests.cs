using SockTuner.Services;

namespace SockTuner.Tests;

/// <summary>
/// Guards the one thing that cannot be caught by reading the code: the shape of the WQL query used
/// to fetch an adapter that a method is then invoked on.
/// </summary>
/// <remarks>
/// A projection such as <c>SELECT InterfaceGuid FROM MSFT_NetAdapter</c> returns objects whose
/// <c>__PATH</c> is empty, because the key properties are missing. Every method call on such an
/// object fails with "Operation is not valid due to the current state of the object" — and the
/// adapter-restart path caught that as a warning, so applying an NDIS keyword reported a restart
/// warning instead of restarting the adapter, and the value never took effect until a reboot. The
/// query looks harmless and reads like an optimisation, which is exactly why it needs a test that
/// fails when somebody narrows it again.
/// </remarks>
public sealed class CimAdapterQueryTests
{
    /// <summary>The keys of MSFT_NetAdapter, as Windows reports them in a full row's path.</summary>
    private static readonly string[] KeyProperties =
        ["CreationClassName", "DeviceID", "SystemCreationClassName", "SystemName"];

    [Theory]
    [InlineData(nameof(CimAdapterSettingStore))]
    [InlineData(nameof(AdapterStateStore))]
    public void TheAdapterLookupIsNotAProjection(string store)
    {
        var query = store == nameof(CimAdapterSettingStore)
            ? CimAdapterSettingStore.AdapterQuery
            : AdapterStateStore.AdapterQuery;

        Assert.Equal("SELECT * FROM MSFT_NetAdapter", query);
    }

    [Theory]
    [InlineData(nameof(CimAdapterSettingStore))]
    [InlineData(nameof(AdapterStateStore))]
    public void ANarrowedLookupWouldDropTheKeysThePathIsBuiltFrom(string store)
    {
        var query = store == nameof(CimAdapterSettingStore)
            ? CimAdapterSettingStore.AdapterQuery
            : AdapterStateStore.AdapterQuery;

        // Either the query selects everything, or it names every key explicitly. Anything else
        // yields a pathless object and silently breaks Enable, Disable and Restart.
        var selectsEverything = query.Contains("SELECT *", StringComparison.OrdinalIgnoreCase);
        var namesEveryKey = KeyProperties.All(key => query.Contains(key, StringComparison.OrdinalIgnoreCase));

        Assert.True(
            selectsEverything || namesEveryKey,
            $"{store} fetches adapters with a projection that omits the key properties "
            + $"({string.Join(", ", KeyProperties)}). InvokeMethod on the result will always fail.");
    }
}
