using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class SettingTransactionServiceTests
{
    private readonly SettingTransactionService _transactions = new();

    [Fact]
    public async Task PrepareApplyAndRollback_RestoresExactOriginalValueAndAbsence()
    {
        var store = new MemorySettingStore();
        var first = SettingCatalog.Get("mmcss.system-responsiveness").ResolveAddress(null);
        var second = SettingCatalog.Get("mmcss.network-throttling-index").ResolveAddress(null);
        store.Values[first] = new StoredSettingValue(true, 20);

        var plan = await _transactions.PrepareAsync(
            [
                new ChangeRequest(first.SettingId, null, 10),
                new ChangeRequest(second.SettingId, null, uint.MaxValue)
            ], store, CancellationToken.None);
        var result = await _transactions.ApplyAsync(plan, store, CancellationToken.None);
        var errors = await _transactions.RollbackAsync(result.Snapshot, store, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(errors);
        Assert.Equal(new StoredSettingValue(true, 20), store.Values[first]);
        Assert.False(store.Values.ContainsKey(second));
    }

    [Fact]
    public async Task Prepare_RefusesUnsupportedCurrentValueSoRollbackRemainsExact()
    {
        var store = new MemorySettingStore();
        var address = SettingCatalog.Get("mmcss.system-responsiveness").ResolveAddress(null);
        store.Values[address] = new StoredSettingValue(true, 15);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _transactions.PrepareAsync(
                [new ChangeRequest(address.SettingId, null, 20)], store, CancellationToken.None));

        Assert.Contains("outside the supported catalog", exception.Message, StringComparison.Ordinal);
        Assert.Equal(new StoredSettingValue(true, 15), store.Values[address]);
    }

    [Fact]
    public async Task Apply_RejectsStalePlanBeforeWriting()
    {
        var store = new MemorySettingStore();
        var address = SettingCatalog.Get("mmcss.system-responsiveness").ResolveAddress(null);
        store.Values[address] = new StoredSettingValue(true, 20);
        var plan = await _transactions.PrepareAsync(
            [new ChangeRequest(address.SettingId, null, 10)], store, CancellationToken.None);
        store.Values[address] = new StoredSettingValue(true, 30);

        var result = await _transactions.ApplyAsync(plan, store, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Snapshot.AppliedSuccessfully);
        Assert.Empty(result.Snapshot.Signature);
        Assert.Equal(new StoredSettingValue(true, 30), store.Values[address]);
        Assert.Contains("Stale plan", result.Error, StringComparison.Ordinal);

        var rollbackErrors = await _transactions.RollbackAsync(result.Snapshot, store, CancellationToken.None);
        Assert.Single(rollbackErrors);
        Assert.Equal(new StoredSettingValue(true, 30), store.Values[address]);
    }

    [Fact]
    public async Task Apply_RevalidatesCallerConstructedPlanValues()
    {
        var store = new MemorySettingStore();
        var definition = SettingCatalog.Get("mmcss.system-responsiveness");
        var address = definition.ResolveAddress(null);
        var forged = new ChangePlan(DateTimeOffset.Now,
            [new PlannedChange(definition, address, StoredSettingValue.Missing, new StoredSettingValue(true, 101))]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _transactions.ApplyAsync(forged, store, CancellationToken.None));
        Assert.Empty(store.Values);
    }

    [Fact]
    public async Task Rollback_RefusesToOverwriteExternalChanges()
    {
        var store = new MemorySettingStore();
        var address = SettingCatalog.Get("mmcss.system-responsiveness").ResolveAddress(null);
        store.Values[address] = new StoredSettingValue(true, 20);
        var plan = await _transactions.PrepareAsync(
            [new ChangeRequest(address.SettingId, null, 10)], store, CancellationToken.None);
        var result = await _transactions.ApplyAsync(plan, store, CancellationToken.None);
        store.Values[address] = new StoredSettingValue(true, 30);

        var errors = await _transactions.RollbackAsync(result.Snapshot, store, CancellationToken.None);

        Assert.Single(errors);
        Assert.Contains("changed externally", errors[0], StringComparison.Ordinal);
        Assert.Equal(new StoredSettingValue(true, 30), store.Values[address]);
    }

    [Fact]
    public async Task Rollback_RejectsSnapshotFromAnotherServiceSession()
    {
        var store = new MemorySettingStore();
        var address = SettingCatalog.Get("mmcss.system-responsiveness").ResolveAddress(null);
        store.Values[address] = new StoredSettingValue(true, 20);
        var plan = await _transactions.PrepareAsync(
            [new ChangeRequest(address.SettingId, null, 10)], store, CancellationToken.None);
        var result = await _transactions.ApplyAsync(plan, store, CancellationToken.None);

        var errors = await new SettingTransactionService()
            .RollbackAsync(result.Snapshot, store, CancellationToken.None);

        Assert.Single(errors);
        Assert.Contains("provenance", errors[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new StoredSettingValue(true, 10), store.Values[address]);
    }

    [Fact]
    public async Task Rollback_RejectsTamperedSnapshot()
    {
        var store = new MemorySettingStore();
        var address = SettingCatalog.Get("mmcss.system-responsiveness").ResolveAddress(null);
        store.Values[address] = new StoredSettingValue(true, 20);
        var plan = await _transactions.PrepareAsync(
            [new ChangeRequest(address.SettingId, null, 10)], store, CancellationToken.None);
        var result = await _transactions.ApplyAsync(plan, store, CancellationToken.None);
        var tamperedChange = result.Snapshot.Changes[0] with { Before = new StoredSettingValue(true, 90) };
        var tamperedSnapshot = result.Snapshot with { Changes = [tamperedChange] };

        var errors = await _transactions.RollbackAsync(tamperedSnapshot, store, CancellationToken.None);

        Assert.Single(errors);
        Assert.Contains("integrity", errors[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new StoredSettingValue(true, 10), store.Values[address]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_RollsBackAllChangesWhenLaterWriteFails(bool failAfterMutation)
    {
        var store = new MemorySettingStore();
        var first = SettingCatalog.Get("mmcss.system-responsiveness").ResolveAddress(null);
        var second = SettingCatalog.Get("mmcss.network-throttling-index").ResolveAddress(null);
        store.Values[first] = new StoredSettingValue(true, 20);
        store.Values[second] = new StoredSettingValue(true, 10);
        var plan = await _transactions.PrepareAsync(
            [
                new ChangeRequest(first.SettingId, null, 10),
                new ChangeRequest(second.SettingId, null, uint.MaxValue)
            ], store, CancellationToken.None);
        if (failAfterMutation)
        {
            store.FailAfterWriteFor = second;
        }
        else
        {
            store.FailWriteFor = second;
        }

        var result = await _transactions.ApplyAsync(plan, store, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(new StoredSettingValue(true, 20), store.Values[first]);
        Assert.Equal(new StoredSettingValue(true, 10), store.Values[second]);
    }

    private sealed class MemorySettingStore : ISettingStore
    {
        public Dictionary<SettingAddress, StoredSettingValue> Values { get; } = [];
        public SettingAddress? FailWriteFor { get; set; }
        public SettingAddress? FailAfterWriteFor { get; set; }

        public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Values.GetValueOrDefault(address, StoredSettingValue.Missing));
        }

        public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (address == FailWriteFor)
            {
                FailWriteFor = null;
                throw new InvalidOperationException("Injected write failure");
            }

            if (value.Exists)
            {
                Values[address] = value;
            }
            else
            {
                Values.Remove(address);
            }

            if (address == FailAfterWriteFor)
            {
                FailAfterWriteFor = null;
                throw new InvalidOperationException("Injected post-write failure");
            }

            return Task.CompletedTask;
        }
    }
}
