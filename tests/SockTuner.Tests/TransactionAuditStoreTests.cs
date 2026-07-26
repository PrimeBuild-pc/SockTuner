using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class TransactionAuditStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"socktuner-audit-{Guid.NewGuid():N}");

    [Fact]
    public void SaveApply_PersistsExactTypedChangeWithoutRegistryAddress()
    {
        var store = new TransactionAuditStore(_directory);
        var result = Result(success: true);

        var saved = store.SaveApply(result);
        var loaded = Assert.Single(store.Load());
        var change = Assert.Single(loaded.Changes);

        Assert.Equal(saved.Id, loaded.Id);
        Assert.Equal(saved.RecordedAt, loaded.RecordedAt);
        Assert.Equal(TransactionAuditOutcome.ApplySucceeded, loaded.Outcome);
        Assert.Equal(result.Snapshot.Id, loaded.SnapshotId);
        Assert.Equal("mmcss.system-responsiveness", change.SettingId);
        Assert.Equal(new AuditStoredValue(true, 20), change.Before);
        Assert.Equal(new AuditStoredValue(true, 10), change.After);
        Assert.DoesNotContain("registryPath", File.ReadAllText(Directory.GetFiles(_directory).Single()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaveRollback_RecordsFailureAndBoundsHistory()
    {
        var store = new TransactionAuditStore(_directory);
        store.SaveApply(Result(success: true), maximumEntries: 2);
        store.SaveApply(Result(success: false), maximumEntries: 2);

        var saved = store.SaveRollback(Result(success: true).Snapshot, ["verification failed"], maximumEntries: 2);
        var entries = store.Load();

        Assert.Equal(2, entries.Count);
        Assert.Equal(TransactionAuditOutcome.RollbackFailed, saved.Outcome);
        Assert.Contains("verification failed", saved.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveApply_DoesNotReportFailureAfterCurrentEntryWasCommitted()
    {
        var store = new TransactionAuditStore(_directory);
        var first = store.SaveApply(Result(success: true));
        var firstPath = Path.Combine(_directory, $"{first.Id:N}.json");
        File.SetAttributes(firstPath, FileAttributes.ReadOnly);
        try
        {
            var second = store.SaveApply(Result(success: true), maximumEntries: 1);

            Assert.True(File.Exists(Path.Combine(_directory, $"{second.Id:N}.json")));
        }
        finally
        {
            File.SetAttributes(firstPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Load_DeletesStructurallyUnknownAudit()
    {
        var store = new TransactionAuditStore(_directory);
        var saved = store.SaveApply(Result(success: true));
        var path = Directory.GetFiles(_directory).Single();
        var json = File.ReadAllText(path).Replace($"\"id\": \"{saved.Id}\",", $"\"id\": \"{saved.Id}\",\n  \"command\": \"cmd.exe\",");
        File.WriteAllText(path, json);

        var entries = store.Load();

        Assert.Empty(entries);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Load_RejectsOmittedExactValue()
    {
        var store = new TransactionAuditStore(_directory);
        store.SaveApply(Result(success: true));
        var path = Directory.GetFiles(_directory).Single();
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"before\": {", "\"omittedBefore\": {"));

        Assert.Empty(store.Load());
        Assert.False(File.Exists(path));
    }

    private static ApplyResult Result(bool success)
    {
        var definition = SettingCatalog.Get("mmcss.system-responsiveness");
        var change = new PlannedChange(
            definition,
            definition.ResolveAddress(null),
            new StoredSettingValue(true, 20),
            new StoredSettingValue(true, 10),
            ChangeSource.Manual);
        var snapshot = new SettingSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), "test", DateTimeOffset.Now, [change], success, success ? "signature" : string.Empty);
        return new ApplyResult(success, snapshot, success ? null : "apply failed", []);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
