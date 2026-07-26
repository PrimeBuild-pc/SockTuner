using System.Text.Json;
using System.Text.Json.Serialization;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class ElevatedWorkerTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task ValidTypedRequest_UsesInjectedStoreAndPersistsApplyAudit()
    {
        var definition = SettingCatalog.Get("mmcss.system-responsiveness");
        var address = definition.ResolveAddress(null);
        var store = new MemorySettingStore { Values = { [address] = new(true, 20) } };
        var request = new ElevatedWorkerRequest(
            ElevatedWorker.SchemaVersion,
            Guid.NewGuid(),
            WorkerOperationKind.Apply,
            [new(definition.Id, null, new(true, 20), new(true, 30), ChangeSource.Manual)]);
        var directory = Path.Combine(Path.GetTempPath(), $"SockTuner-worker-{Guid.NewGuid():N}");
        using var output = new StringWriter();
        try
        {
            var exitCode = await ElevatedWorker.RunAsync(
                new StringReader(JsonSerializer.Serialize(request, Options)),
                output,
                CancellationToken.None,
                store,
                new TransactionAuditStore(directory));
            var response = JsonSerializer.Deserialize<ElevatedWorkerResponse>(output.ToString(), Options)!;

            Assert.Equal(0, exitCode);
            Assert.True(response.Success);
            Assert.Equal(new StoredSettingValue(true, 30), store.Values[address]);
            Assert.Equal(TransactionAuditOutcome.ApplySucceeded,
                Assert.Single(new TransactionAuditStore(directory).Load()).Outcome);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_RollbackRequestRefusesDriftAndPersistsFailure()
    {
        var definition = SettingCatalog.Get("mmcss.system-responsiveness");
        var address = definition.ResolveAddress(null);
        var store = new MemorySettingStore { Values = { [address] = new(true, 40) } };
        var directory = Path.Combine(Path.GetTempPath(), $"SockTuner-worker-{Guid.NewGuid():N}");
        try
        {
            var result = await ElevatedWorker.ExecuteAsync(
                new(ElevatedWorker.SchemaVersion, Guid.NewGuid(), WorkerOperationKind.Rollback,
                    [new(definition.Id, null, new(true, 30), new(true, 20), ChangeSource.Recovery)]),
                store,
                new TransactionAuditStore(directory),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(new StoredSettingValue(true, 40), store.Values[address]);
            Assert.Equal(TransactionAuditOutcome.RollbackFailed,
                Assert.Single(new TransactionAuditStore(directory).Load()).Outcome);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_SuccessfulRollbackPersistsRollbackAudit()
    {
        var definition = SettingCatalog.Get("mmcss.system-responsiveness");
        var address = definition.ResolveAddress(null);
        var store = new MemorySettingStore { Values = { [address] = new(true, 30) } };
        var directory = Path.Combine(Path.GetTempPath(), $"SockTuner-worker-{Guid.NewGuid():N}");
        try
        {
            var result = await ElevatedWorker.ExecuteAsync(
                new(ElevatedWorker.SchemaVersion, Guid.NewGuid(), WorkerOperationKind.Rollback,
                    [new(definition.Id, null, new(true, 30), new(true, 20), ChangeSource.Recovery)]),
                store,
                new TransactionAuditStore(directory),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(new StoredSettingValue(true, 20), store.Values[address]);
            Assert.Equal(TransactionAuditOutcome.RollbackSucceeded,
                Assert.Single(new TransactionAuditStore(directory).Load()).Outcome);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_AuditFailureCompensatesVerifiedMutation()
    {
        var definition = SettingCatalog.Get("mmcss.system-responsiveness");
        var address = definition.ResolveAddress(null);
        var store = new MemorySettingStore { Values = { [address] = new(true, 20) } };
        var file = Path.GetTempFileName();
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ElevatedWorker.ExecuteAsync(
                new(ElevatedWorker.SchemaVersion, Guid.NewGuid(), WorkerOperationKind.Apply,
                    [new(definition.Id, null, new(true, 20), new(true, 30), ChangeSource.Manual)]),
                store,
                new TransactionAuditStore(file),
                CancellationToken.None));

            Assert.Contains("restored", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new StoredSettingValue(true, 20), store.Values[address]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Execute_ReportsFailedInternalRollbackWhenAuditAlsoFails()
    {
        var responsiveness = SettingCatalog.Get("mmcss.system-responsiveness");
        var responsivenessAddress = responsiveness.ResolveAddress(null);
        var throttling = SettingCatalog.Get("mmcss.network-throttling-index");
        var throttlingAddress = throttling.ResolveAddress(null);
        var store = new MemorySettingStore
        {
            Values =
            {
                [responsivenessAddress] = new(true, 20),
                [throttlingAddress] = new(true, 10)
            },
            FailWriteNumbers = { 2, 3 }
        };
        var file = Path.GetTempFileName();
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ElevatedWorker.ExecuteAsync(
                new(ElevatedWorker.SchemaVersion, Guid.NewGuid(), WorkerOperationKind.Apply,
                [
                    new(responsiveness.Id, null, new(true, 20), new(true, 30), ChangeSource.Manual),
                    new(throttling.Id, null, new(true, 10), new(true, 20), ChangeSource.Manual)
                ]),
                store,
                new TransactionAuditStore(file),
                CancellationToken.None));

            Assert.Contains("Compensation errors", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Injected write failure", exception.Message, StringComparison.Ordinal);
            Assert.Equal(new StoredSettingValue(true, 20), store.Values[responsivenessAddress]);
            Assert.Equal(new StoredSettingValue(true, 20), store.Values[throttlingAddress]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[],\"Command\":\"cmd.exe\"}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[{\"SettingId\":\"arbitrary.registry.path\",\"TargetId\":null,\"Expected\":{\"Exists\":false,\"Value\":0},\"Desired\":{\"Exists\":true,\"Value\":1},\"Source\":\"Manual\"}]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Changes\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":1,\"Changes\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Unknown\",\"Changes\":[{\"SettingId\":\"mmcss.system-responsiveness\",\"TargetId\":null,\"Expected\":{\"Exists\":true,\"Value\":20},\"Desired\":{\"Exists\":true,\"Value\":10},\"Source\":\"Manual\"}]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[null]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[{\"SettingId\":\"mmcss.system-responsiveness\",\"TargetId\":null,\"Expected\":{\"Exists\":true,\"Value\":20,\"Path\":\"HKLM\"},\"Desired\":{\"Exists\":true,\"Value\":10},\"Source\":\"Manual\"}]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[{\"SettingId\":\"mmcss.system-responsiveness\",\"TargetId\":null,\"Expected\":{\"Exists\":true,\"Value\":20},\"Desired\":{\"Exists\":true,\"Value\":10}}]}")]
    public async Task UnlistedOrStructurallyUnknownInput_IsRejected(string json)
    {
        using var output = new StringWriter();

        var exitCode = await ElevatedWorker.RunAsync(new StringReader(json), output, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("Rejected typed request", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedRequest_IsRejected()
    {
        using var output = new StringWriter();

        var exitCode = await ElevatedWorker.RunAsync(
            new StringReader(new string('x', ElevatedWorker.MaximumRequestCharacters + 1)),
            output,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
    }

    private sealed class MemorySettingStore : ISettingStore
    {
        private int _writeCount;
        public Dictionary<SettingAddress, StoredSettingValue> Values { get; } = [];
        public HashSet<int> FailWriteNumbers { get; } = [];

        public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Values.GetValueOrDefault(address, StoredSettingValue.Missing));
        }

        public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWriteNumbers.Contains(++_writeCount)) throw new InvalidOperationException("Injected write failure");
            if (value.Exists) Values[address] = value;
            else Values.Remove(address);
            return Task.CompletedTask;
        }
    }
}
