using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SockTuner.Models;

namespace SockTuner.Persistence;

public enum TransactionAuditOutcome
{
    Unknown,
    ApplySucceeded,
    ApplyFailed,
    RollbackSucceeded,
    RollbackFailed
}

public sealed record AuditStoredValue(
    [property: JsonRequired] bool Exists,
    [property: JsonRequired] string Value);

public sealed record AuditedSettingChange(
    [property: JsonRequired] string SettingId,
    [property: JsonRequired] string? TargetId,
    [property: JsonRequired] AuditStoredValue Before,
    [property: JsonRequired] AuditStoredValue After,
    [property: JsonRequired] ChangeSource Source);

public sealed record TransactionAuditEntry(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] DateTimeOffset RecordedAt,
    [property: JsonRequired] TransactionAuditOutcome Outcome,
    [property: JsonRequired] Guid SnapshotId,
    [property: JsonRequired] IReadOnlyList<AuditedSettingChange> Changes,
    [property: JsonRequired] string? Error);

public sealed class TransactionAuditStore
{
    // 2: setting values became text so NDIS REG_SZ keywords can be audited exactly.
    // Schema 1 entries are rejected by Validate and removed on the next Load.
    private const int SchemaVersion = 2;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _directory;

    public TransactionAuditStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PrimeBuild", "SockTuner", "TransactionAudit"))
    { }

    internal TransactionAuditStore(string directory) => _directory = directory;

    public TransactionAuditEntry SaveApply(ApplyResult result, int maximumEntries = 100) => Save(
        result.Success ? TransactionAuditOutcome.ApplySucceeded : TransactionAuditOutcome.ApplyFailed,
        result.Snapshot,
        result.Error,
        maximumEntries);

    public TransactionAuditEntry SaveRollback(
        SettingSnapshot snapshot,
        IReadOnlyList<string> errors,
        int maximumEntries = 100) => Save(
            errors.Count == 0 ? TransactionAuditOutcome.RollbackSucceeded : TransactionAuditOutcome.RollbackFailed,
            snapshot,
            errors.Count == 0 ? null : string.Join("; ", errors),
            maximumEntries);

    public IReadOnlyList<TransactionAuditEntry> Load()
    {
        if (!Directory.Exists(_directory)) return [];
        try
        {
            return Directory.GetFiles(_directory, "*.json").Select(TryLoad)
                .Where(entry => entry is not null)
                .Cast<TransactionAuditEntry>()
                .OrderByDescending(entry => entry.RecordedAt)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private TransactionAuditEntry Save(
        TransactionAuditOutcome outcome,
        SettingSnapshot snapshot,
        string? error,
        int maximumEntries)
    {
        maximumEntries = Math.Clamp(maximumEntries, 1, 1000);
        var entry = new TransactionAuditEntry(
            SchemaVersion,
            Guid.NewGuid(),
            DateTimeOffset.Now,
            outcome,
            snapshot.Id,
            snapshot.Changes.Select(change => new AuditedSettingChange(
                change.Definition.Id,
                change.Address.TargetId,
                new AuditStoredValue(change.Before.Exists, change.Before.Value),
                new AuditStoredValue(change.After.Exists, change.After.Value),
                change.Source)).ToArray(),
            error);
        Validate(entry);
        Directory.CreateDirectory(_directory);
        var path = PathFor(entry.Id);
        var temporaryPath = $"{path}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entry, Options));
            File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        foreach (var old in Load().Skip(maximumEntries))
        {
            try { File.Delete(PathFor(old.Id)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return entry;
    }

    private TransactionAuditEntry? TryLoad(string path)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<TransactionAuditEntry>(File.ReadAllText(path), Options);
            if (entry is null || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var fileId)
                || entry.Id != fileId)
            {
                throw new InvalidDataException("Audit identity does not match its file.");
            }

            Validate(entry);
            return entry;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                          or JsonException or InvalidDataException
                                          or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            try { File.Delete(path); } catch (Exception deleteException) when (deleteException is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }

    private static void Validate(TransactionAuditEntry entry)
    {
        if (entry.SchemaVersion != SchemaVersion || entry.Id == Guid.Empty || entry.SnapshotId == Guid.Empty
            || entry.RecordedAt == default
            || entry.Outcome == TransactionAuditOutcome.Unknown || !Enum.IsDefined(entry.Outcome)
            || entry.Changes is not { Count: > 0 and <= 100 })
        {
            throw new InvalidDataException("Transaction audit envelope is invalid.");
        }

        // Structural checks only. History records what happened, so it must stay readable after a
        // driver update removes a keyword or the catalog changes; anything replayed from here goes
        // back through the transaction service, which re-validates against the live system.
        var targets = new HashSet<(string SettingId, string? TargetId)>();
        foreach (var change in entry.Changes)
        {
            if (change is null || change.Before is null || change.After is null
                || string.IsNullOrWhiteSpace(change.SettingId)
                || change.Source == ChangeSource.Unknown || !Enum.IsDefined(change.Source)
                || change.Before.Value is null || change.After.Value is null
                || (!change.Before.Exists && change.Before.Value.Length != 0)
                || (!change.After.Exists && change.After.Value.Length != 0)
                || !targets.Add((change.SettingId, change.TargetId)))
            {
                throw new InvalidDataException("Transaction audit change is invalid.");
            }
        }
    }

    private string PathFor(Guid id) => Path.Combine(_directory, $"{id:N}.json");
}
