using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

/// <summary>
/// The NIC path has no static allowlist: the driver's advertised capabilities are the
/// allowlist. These cover the full read -> plan -> apply -> verify -> rollback -> verify cycle
/// and the refusals that keep a forged or stale plan from reaching a real adapter.
/// </summary>
public sealed class NicSettingTransactionTests
{
    private static readonly Guid AdapterId = Guid.Parse("DBE23C40-A216-4351-BC0F-CBF9519BC5CE");
    private static readonly Guid OtherAdapterId = Guid.Parse("FE873087-80FE-4C36-8C66-5532CC57800B");

    [Fact]
    public async Task EnumeratedKeyword_CompletesApplyVerifyRollbackCycle()
    {
        var capability = Enumerated("*InterruptModeration", "1", "0", "1");
        var transactions = Service(capability);
        var store = StoreFor(capability);
        var address = new NicSettingSpecification(capability).ResolveAddress(AdapterId.ToString());

        var plan = await transactions.PrepareAsync(
            [new ChangeRequest(capability.SettingId, AdapterId.ToString(), "0")], store, CancellationToken.None);
        var result = await transactions.ApplyAsync(plan, store, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(new StoredSettingValue(true, "0"), store.Values[address]);

        var errors = await transactions.RollbackAsync(result.Snapshot, store, CancellationToken.None);
        Assert.Empty(errors);
        Assert.Equal(new StoredSettingValue(true, "1"), store.Values[address]);
    }

    [Fact]
    public async Task NumericRangeKeyword_AppliesValueOnTheAdvertisedStep()
    {
        var capability = Range("*ReceiveBuffers", "512", 32, 4096, 8);
        var transactions = Service(capability);
        var store = StoreFor(capability);
        var address = new NicSettingSpecification(capability).ResolveAddress(AdapterId.ToString());

        var plan = await transactions.PrepareAsync(
            [new ChangeRequest(capability.SettingId, AdapterId.ToString(), "1024")], store, CancellationToken.None);
        var result = await transactions.ApplyAsync(plan, store, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new StoredSettingValue(true, "1024"), store.Values[address]);
    }

    [Fact]
    public async Task Prepare_RefusesValueTheDriverDoesNotAdvertise()
    {
        var capability = Enumerated("*FlowControl", "3", "0", "3");
        var transactions = Service(capability);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => transactions.PrepareAsync(
            [new ChangeRequest(capability.SettingId, AdapterId.ToString(), "4")],
            StoreFor(capability),
            CancellationToken.None));
    }

    [Fact]
    public async Task Apply_RevalidatesForgedPlanAgainstTheDriver()
    {
        var capability = Enumerated("*FlowControl", "3", "0", "3");
        var transactions = Service(capability);
        var store = StoreFor(capability);
        var specification = new NicSettingSpecification(capability);
        var forged = new ChangePlan(DateTimeOffset.Now,
        [
            new PlannedChange(
                specification,
                specification.ResolveAddress(AdapterId.ToString()),
                new StoredSettingValue(true, "3"),
                new StoredSettingValue(true, "4"))
        ]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            transactions.ApplyAsync(forged, store, CancellationToken.None));
        Assert.Equal(new StoredSettingValue(true, "3"), store.Values.Values.Single());
    }

    [Fact]
    public async Task Prepare_RefusesToRemoveANicProperty()
    {
        var capability = Enumerated("*InterruptModeration", "1", "0", "1");
        var transactions = Service(capability);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => transactions.PrepareAsync(
            [new ChangeRequest(capability.SettingId, AdapterId.ToString(), null)],
            StoreFor(capability),
            CancellationToken.None));

        Assert.Contains("cannot be removed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_RefusesKeywordTheAdapterNoLongerAdvertises()
    {
        var transactions = Service(Enumerated("*InterruptModeration", "1", "0", "1"));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => transactions.PrepareAsync(
            [new ChangeRequest("nic.*JumboPacket", AdapterId.ToString(), "9014")],
            new MemorySettingStore(),
            CancellationToken.None));
    }

    [Fact]
    public async Task Prepare_RefusesKeywordBelongingToADifferentAdapter()
    {
        var transactions = Service(Enumerated("*InterruptModeration", "1", "0", "1"));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => transactions.PrepareAsync(
            [new ChangeRequest("nic.*InterruptModeration", OtherAdapterId.ToString(), "0")],
            new MemorySettingStore(),
            CancellationToken.None));
    }

    [Fact]
    public void ResolveAddress_RejectsAdapterThatDoesNotOwnTheCapability()
    {
        var specification = new NicSettingSpecification(Enumerated("*InterruptModeration", "1", "0", "1"));

        Assert.Throws<ArgumentException>(() => specification.ResolveAddress(OtherAdapterId.ToString()));
        Assert.Throws<ArgumentException>(() => specification.ResolveAddress(null));
        Assert.Throws<ArgumentException>(() => specification.ResolveAddress("not-a-guid"));
    }

    [Fact]
    public void Resolve_MatchesKeywordCasingAcrossVendors()
    {
        var intelStyle = Enumerated("*PriorityVLANTag", "3", "0", "1", "2", "3");

        // A plan written against the Realtek spelling still resolves on an Intel adapter.
        var specification = NicSettingSpecification.Resolve(
            "nic.*priorityvlantag", AdapterId.ToString(), [intelStyle]);

        Assert.Equal("*PriorityVLANTag", specification.Capability.Keyword);
    }

    [Fact]
    public async Task MixedPlan_AppliesNicAndRegistrySettingsInOneVerifiedTransaction()
    {
        var capability = Enumerated("*InterruptModeration", "1", "0", "1");
        var transactions = Service(capability);
        var store = StoreFor(capability);
        var mmcss = SettingCatalog.Get("mmcss.system-responsiveness");
        store.Values[mmcss.ResolveAddress(null)] = new StoredSettingValue(true, "20");

        var plan = await transactions.PrepareAsync(
            [
                new ChangeRequest(mmcss.Id, null, "10"),
                new ChangeRequest(capability.SettingId, AdapterId.ToString(), "0")
            ], store, CancellationToken.None);
        var result = await transactions.ApplyAsync(plan, store, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Snapshot.Changes.Count);

        var errors = await transactions.RollbackAsync(result.Snapshot, store, CancellationToken.None);
        Assert.Empty(errors);
        Assert.Equal(new StoredSettingValue(true, "20"), store.Values[mmcss.ResolveAddress(null)]);
        Assert.Equal(
            new StoredSettingValue(true, "1"),
            store.Values[new NicSettingSpecification(capability).ResolveAddress(AdapterId.ToString())]);
    }

    private static SettingTransactionService Service(params AdapterSettingCapability[] capabilities) =>
        new(SettingSpecifications.From(capabilities));

    private static MemorySettingStore StoreFor(AdapterSettingCapability capability)
    {
        var store = new MemorySettingStore();
        store.Values[new NicSettingSpecification(capability).ResolveAddress(AdapterId.ToString())] =
            new StoredSettingValue(true, capability.CurrentValue);
        return store;
    }

    private static AdapterSettingCapability Enumerated(string keyword, string current, params string[] values) =>
        Capability(keyword, current) with
        {
            Choices = values.Select(value => new CapabilityChoice(value, value)).ToArray()
        };

    private static AdapterSettingCapability Range(
        string keyword, string current, long minimum, long maximum, long step) =>
        Capability(keyword, current) with { Minimum = minimum, Maximum = maximum, Step = step };

    private static AdapterSettingCapability Capability(string keyword, string current)
    {
        var profile = NicKeywordCatalog.For(keyword);
        return new AdapterSettingCapability(
            AdapterId, "Ethernet 2", "Intel(R) Ethernet Controller I226-V", keyword, keyword,
            current, null, [], null, null, null,
            AdapterSettingCapability.RegistrySz, false,
            profile.Areas, profile.Risk, profile.TradeOff);
    }

    private sealed class MemorySettingStore : ISettingStore
    {
        public Dictionary<SettingAddress, StoredSettingValue> Values { get; } = [];

        public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Values.GetValueOrDefault(address, StoredSettingValue.Missing));
        }

        public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
