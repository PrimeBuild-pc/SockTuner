using System.Text.Json;
using System.Text.Json.Serialization;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;

namespace SockTuner.Tests;

/// <summary>
/// The worker is the privilege boundary. It re-resolves every NIC keyword against the driver
/// inside the elevated process, so a caller cannot widen what it is allowed to write by lying
/// about the capability.
/// </summary>
public sealed class ElevatedWorkerNicTests : IDisposable
{
    private static readonly Guid AdapterId = Guid.Parse("DBE23C40-A216-4351-BC0F-CBF9519BC5CE");
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _auditDirectory =
        Path.Combine(Path.GetTempPath(), $"socktuner-worker-nic-{Guid.NewGuid():N}");

    [Fact]
    public async Task AdvertisedKeyword_IsAppliedVerifiedAndAudited()
    {
        var capability = Capability("*InterruptModeration", "1", "0", "1");
        var store = StoreWith(capability);
        var output = new StringWriter();

        var exitCode = await ElevatedWorker.RunAsync(
            Request(capability.SettingId, "1", "0"),
            output,
            CancellationToken.None,
            store,
            new TransactionAuditStore(_auditDirectory),
            SettingSpecifications.From([capability]));

        var response = Deserialize(output);
        Assert.Equal(0, exitCode);
        Assert.True(response.Success);
        Assert.Equal(new StoredSettingValue(true, "0"), store.Values.Values.Single());
        Assert.Equal(
            TransactionAuditOutcome.ApplySucceeded,
            Assert.Single(new TransactionAuditStore(_auditDirectory).Load()).Outcome);
    }

    [Fact]
    public async Task KeywordTheDriverDoesNotAdvertise_IsRejected()
    {
        var advertised = Capability("*InterruptModeration", "1", "0", "1");
        var output = new StringWriter();

        var exitCode = await ElevatedWorker.RunAsync(
            Request("nic.*JumboPacket", "1514", "9014"),
            output,
            CancellationToken.None,
            new MemorySettingStore(),
            new TransactionAuditStore(_auditDirectory),
            SettingSpecifications.From([advertised]));

        Assert.Equal(2, exitCode);
        Assert.Contains("does not advertise", Deserialize(output).Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValueOutsideTheDriversAdvertisedSet_IsRejectedEvenWhenTheCallerClaimsItIsValid()
    {
        // The caller asks for *FlowControl = 4, which Intel advertises but this Realtek does not.
        var capability = Capability("*FlowControl", "3", "0", "3");
        var store = StoreWith(capability);
        var output = new StringWriter();

        var exitCode = await ElevatedWorker.RunAsync(
            Request(capability.SettingId, "3", "4"),
            output,
            CancellationToken.None,
            store,
            new TransactionAuditStore(_auditDirectory),
            SettingSpecifications.From([capability]));

        Assert.Equal(2, exitCode);
        Assert.Equal(new StoredSettingValue(true, "3"), store.Values.Values.Single());
        Assert.Empty(new TransactionAuditStore(_auditDirectory).Load());
    }

    [Fact]
    public async Task RemovingANicPropertyIsRejected()
    {
        var capability = Capability("*InterruptModeration", "1", "0", "1");
        var output = new StringWriter();

        var exitCode = await ElevatedWorker.RunAsync(
            Request(capability.SettingId, "1", desired: null),
            output,
            CancellationToken.None,
            StoreWith(capability),
            new TransactionAuditStore(_auditDirectory),
            SettingSpecifications.From([capability]));

        Assert.Equal(2, exitCode);
        Assert.Contains("cannot be removed", Deserialize(output).Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestAndResponseAreNewlineDelimitedSoOnePipeCarriesOneMessage()
    {
        var capability = Capability("*InterruptModeration", "1", "0", "1");
        var output = new StringWriter();

        await ElevatedWorker.RunAsync(
            Request(capability.SettingId, "1", "0"),
            output,
            CancellationToken.None,
            StoreWith(capability),
            new TransactionAuditStore(_auditDirectory),
            SettingSpecifications.From([capability]));

        var payload = output.ToString();
        Assert.EndsWith(Environment.NewLine, payload, StringComparison.Ordinal);
        Assert.Single(payload.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task TrailingContentAfterTheRequestLineIsNotExecuted()
    {
        var capability = Capability("*InterruptModeration", "1", "0", "1");
        var store = StoreWith(capability);
        var first = JsonSerializer.Serialize(
            RequestObject(capability.SettingId, "1", "0"), Options);
        var second = JsonSerializer.Serialize(
            RequestObject(capability.SettingId, "0", "1"), Options);
        var output = new StringWriter();

        await ElevatedWorker.RunAsync(
            new StringReader($"{first}\n{second}\n"),
            output,
            CancellationToken.None,
            store,
            new TransactionAuditStore(_auditDirectory),
            SettingSpecifications.From([capability]));

        // Only the first line is honoured; the second request never runs.
        Assert.Equal(new StoredSettingValue(true, "0"), store.Values.Values.Single());
    }

    private static StringReader Request(string settingId, string expected, string? desired) =>
        new(JsonSerializer.Serialize(RequestObject(settingId, expected, desired), Options) + "\n");

    private static ElevatedWorkerRequest RequestObject(string settingId, string expected, string? desired) =>
        new(
            ElevatedWorker.SchemaVersion,
            Guid.NewGuid(),
            WorkerOperationKind.Apply,
            [
                new WorkerSettingOperation(
                    settingId,
                    AdapterId.ToString(),
                    new WorkerStoredValue(true, expected),
                    desired is null ? new WorkerStoredValue(false, "") : new WorkerStoredValue(true, desired),
                    ChangeSource.Manual)
            ]);

    private static ElevatedWorkerResponse Deserialize(StringWriter output) =>
        JsonSerializer.Deserialize<ElevatedWorkerResponse>(output.ToString(), Options)!;

    private static MemorySettingStore StoreWith(AdapterSettingCapability capability)
    {
        var store = new MemorySettingStore();
        store.Values[new NicSettingSpecification(capability).ResolveAddress(AdapterId.ToString())] =
            new StoredSettingValue(true, capability.CurrentValue);
        return store;
    }

    private static AdapterSettingCapability Capability(string keyword, string current, params string[] values)
    {
        var profile = NicKeywordCatalog.For(keyword);
        return new AdapterSettingCapability(
            AdapterId, "Ethernet 2", "Test adapter", keyword, keyword, current, null,
            values.Select(value => new CapabilityChoice(value, value)).ToArray(),
            null, null, null, AdapterSettingCapability.RegistrySz, false,
            profile.Areas, profile.Risk, profile.TradeOff);
    }

    public void Dispose()
    {
        if (Directory.Exists(_auditDirectory)) Directory.Delete(_auditDirectory, recursive: true);
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
            if (value.Exists) Values[address] = value;
            else Values.Remove(address);
            return Task.CompletedTask;
        }
    }
}
