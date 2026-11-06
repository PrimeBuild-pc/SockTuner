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
        var store = new MemorySettingStore { Values = { [address] = new(true, "20") } };
        var request = new ElevatedWorkerRequest(
            ElevatedWorker.SchemaVersion,
            Guid.NewGuid(),
            WorkerOperationKind.Apply,
            [new(definition.Id, null, new(true, "20"), new(true, "30"), ChangeSource.Manual)]);
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
            Assert.Equal(new StoredSettingValue(true, "30"), store.Values[address]);
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
        var store = new MemorySettingStore { Values = { [address] = new(true, "40") } };
        var directory = Path.Combine(Path.GetTempPath(), $"SockTuner-worker-{Guid.NewGuid():N}");
        try
        {
            var result = await ElevatedWorker.ExecuteAsync(
                new(ElevatedWorker.SchemaVersion, Guid.NewGuid(), WorkerOperationKind.Rollback,
                    [new(definition.Id, null, new(true, "30"), new(true, "20"), ChangeSource.Recovery)]),
                store,
                new TransactionAuditStore(directory),
                CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(new StoredSettingValue(true, "40"), store.Values[address]);
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
        var store = new MemorySettingStore { Values = { [address] = new(true, "30") } };
        var directory = Path.Combine(Path.GetTempPath(), $"SockTuner-worker-{Guid.NewGuid():N}");
        try
        {
            var result = await ElevatedWorker.ExecuteAsync(
                new(ElevatedWorker.SchemaVersion, Guid.NewGuid(), WorkerOperationKind.Rollback,
                    [new(definition.Id, null, new(true, "30"), new(true, "20"), ChangeSource.Recovery)]),
                store,
                new TransactionAuditStore(directory),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(new StoredSettingValue(true, "20"), store.Values[address]);
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
        var store = new MemorySettingStore { Values = { [address] = new(true, "20") } };
        var file = Path.GetTempFileName();
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ElevatedWorker.ExecuteAsync(
                new(ElevatedWorker.SchemaVersion, Guid.NewGuid(), WorkerOperationKind.Apply,
                    [new(definition.Id, null, new(true, "20"), new(true, "30"), ChangeSource.Manual)]),
                store,
                new TransactionAuditStore(file),
                CancellationToken.None));

            Assert.Contains("restored", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new StoredSettingValue(true, "20"), store.Values[address]);
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
                [responsivenessAddress] = new(true, "20"),
                [throttlingAddress] = new(true, "10")
            },
            FailWriteNumbers = { 2, 3 }
        };
        var file = Path.GetTempFileName();
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ElevatedWorker.ExecuteAsync(
                new(ElevatedWorker.SchemaVersion, Guid.NewGuid(), WorkerOperationKind.Apply,
                [
                    new(responsiveness.Id, null, new(true, "20"), new(true, "30"), ChangeSource.Manual),
                    new(throttling.Id, null, new(true, "10"), new(true, "20"), ChangeSource.Manual)
                ]),
                store,
                new TransactionAuditStore(file),
                CancellationToken.None));

            Assert.Contains("Compensation errors", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Injected write failure", exception.Message, StringComparison.Ordinal);
            Assert.Equal(new StoredSettingValue(true, "20"), store.Values[responsivenessAddress]);
            Assert.Equal(new StoredSettingValue(true, "20"), store.Values[throttlingAddress]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[],\"Command\":\"cmd.exe\"}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[{\"SettingId\":\"arbitrary.registry.path\",\"TargetId\":null,\"Expected\":{\"Exists\":false,\"Value\":\"\"},\"Desired\":{\"Exists\":true,\"Value\":\"1\"},\"Source\":\"Manual\"}]}")]
    // An absent value must carry no text, so a non-empty value with Exists:false is refused.
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[{\"SettingId\":\"mmcss.system-responsiveness\",\"TargetId\":null,\"Expected\":{\"Exists\":false,\"Value\":\"20\"},\"Desired\":{\"Exists\":true,\"Value\":\"10\"},\"Source\":\"Manual\"}]}")]
    // Non-canonical numeric text would read back differently and break exact verification.
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[{\"SettingId\":\"mmcss.system-responsiveness\",\"TargetId\":null,\"Expected\":{\"Exists\":true,\"Value\":\"20\"},\"Desired\":{\"Exists\":true,\"Value\":\"010\"},\"Source\":\"Manual\"}]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Changes\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":1,\"Changes\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Unknown\",\"Changes\":[{\"SettingId\":\"mmcss.system-responsiveness\",\"TargetId\":null,\"Expected\":{\"Exists\":true,\"Value\":\"20\"},\"Desired\":{\"Exists\":true,\"Value\":\"10\"},\"Source\":\"Manual\"}]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[null]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[{\"SettingId\":\"mmcss.system-responsiveness\",\"TargetId\":null,\"Expected\":{\"Exists\":true,\"Value\":\"20\",\"Path\":\"HKLM\"},\"Desired\":{\"Exists\":true,\"Value\":\"10\"},\"Source\":\"Manual\"}]}")]
    [InlineData("{\"SchemaVersion\":1,\"RequestId\":\"00000000-0000-0000-0000-000000000001\",\"Operation\":\"Apply\",\"Changes\":[{\"SettingId\":\"mmcss.system-responsiveness\",\"TargetId\":null,\"Expected\":{\"Exists\":true,\"Value\":\"20\"},\"Desired\":{\"Exists\":true,\"Value\":\"10\"}}]}")]
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

    [Theory]
    [InlineData("qos")]
    [InlineData("dns")]
    [InlineData("cim")]
    public async Task DedicatedBackendsDoNotPassThroughTheDwordCatalog(string backend)
    {
        var adapterId = Guid.NewGuid().ToString();
        var global = new GlobalSettingCapability(CimGlobalPropertyCatalog.TcpSettingClass, "InternetCustom",
            "AutoTuningLevelLocal", "Receive window", "TCP", "Normal",
            [new("Normal", "Normal"), new("Disabled", "Disabled")], null, null,
            EvidenceLevel.Documented, ChangeRisk.Medium, "None", "Test");
        var settingId = backend switch { "qos" => QosPolicySpecification.SettingId, "dns" => DnsServerSpecification.SettingId, _ => global.SettingId };
        var target = backend switch { "qos" => QosPolicySpecification.NameFor("worker-canary.exe"), "dns" => adapterId, _ => global.InstanceKey };
        var desired = backend switch { "qos" => new QosPolicyValue(46, "worker-canary.exe", "UDP", "50000-50001").Canonical, "dns" => "1.1.1.1", _ => "Disabled" };
        var before = backend == "cim" ? new StoredSettingValue(true, "Normal") : StoredSettingValue.Missing;
        var resolve = SettingSpecifications.From([], [global]);
        var address = resolve(settingId, target).ResolveAddress(target);
        var store = new MemorySettingStore();
        if (before.Exists) store.Values[address] = before;
        var directory = Path.Combine(Path.GetTempPath(), $"SockTuner-worker-backend-{Guid.NewGuid():N}");
        try
        {
            async Task<ElevatedWorkerResponse> Run(WorkerOperationKind operation, WorkerStoredValue expected, WorkerStoredValue value)
            {
                var request = new ElevatedWorkerRequest(ElevatedWorker.SchemaVersion, Guid.NewGuid(), operation,
                    [new(settingId, target, expected, value, ChangeSource.Manual)]);
                using var output = new StringWriter();
                var code = await ElevatedWorker.RunAsync(new StringReader(JsonSerializer.Serialize(request, Options)), output,
                    CancellationToken.None, store, new TransactionAuditStore(directory), resolve);
                var response = JsonSerializer.Deserialize<ElevatedWorkerResponse>(output.ToString(), Options)!;
                Assert.True(response.Success, response.Status);
                Assert.Equal(0, code);
                return response;
            }
            await Run(WorkerOperationKind.Apply, new(before.Exists, before.Value), new(true, desired));
            Assert.Equal(new StoredSettingValue(true, desired), await store.ReadAsync(address, CancellationToken.None));
            await Run(WorkerOperationKind.Rollback, new(true, desired), new(before.Exists, before.Value));
            Assert.Equal(before, await store.ReadAsync(address, CancellationToken.None));
            var audit = new TransactionAuditStore(directory).Load();
            Assert.Contains(audit, item => item.Outcome == TransactionAuditOutcome.ApplySucceeded);
            Assert.Contains(audit, item => item.Outcome == TransactionAuditOutcome.RollbackSucceeded);
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
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
