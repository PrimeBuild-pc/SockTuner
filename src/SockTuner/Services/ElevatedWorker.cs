using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SockTuner.Models;
using SockTuner.Persistence;

namespace SockTuner.Services;

public enum WorkerOperationKind
{
    Unknown,
    Apply,
    Rollback
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerStoredValue(
    [property: JsonRequired] bool Exists,
    [property: JsonRequired] uint Value);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkerSettingOperation(
    [property: JsonRequired] string SettingId,
    [property: JsonRequired] string? TargetId,
    [property: JsonRequired] WorkerStoredValue Expected,
    [property: JsonRequired] WorkerStoredValue Desired,
    [property: JsonRequired] ChangeSource Source);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ElevatedWorkerRequest(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] WorkerOperationKind Operation,
    [property: JsonRequired] IReadOnlyList<WorkerSettingOperation> Changes);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ElevatedWorkerResponse(int SchemaVersion, Guid RequestId, bool Success, string Status);

internal static class ElevatedWorker
{
    internal const int SchemaVersion = 1;
    internal const int MaximumRequestCharacters = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal static async Task<int> RunAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken,
        ISettingStore? store = null,
        TransactionAuditStore? auditStore = null)
    {
        ElevatedWorkerRequest? request = null;
        try
        {
            var buffer = new char[MaximumRequestCharacters + 1];
            var count = await input.ReadBlockAsync(buffer, cancellationToken);
            if (count == buffer.Length)
            {
                throw new InvalidDataException("Worker request exceeds the size limit.");
            }

            request = JsonSerializer.Deserialize<ElevatedWorkerRequest>(buffer.AsSpan(0, count), Options)
                ?? throw new InvalidDataException("Worker request is empty.");
            Validate(request);
            var result = await ExecuteAsync(
                request,
                store ?? WindowsRegistrySettingStore.CreateForIsolatedVm(),
                auditStore ?? new TransactionAuditStore(),
                cancellationToken);
            await WriteAsync(output, new(SchemaVersion, request.RequestId, result.Success,
                result.Success ? "Typed operation applied and verified." : result.Error ?? "Typed operation failed."));
            return result.Success ? 0 : 3;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException
                                          or ArgumentException or InvalidOperationException or KeyNotFoundException
                                          or UnauthorizedAccessException or IOException)
        {
            await WriteAsync(output, new(SchemaVersion, request?.RequestId ?? Guid.Empty, false,
                $"Rejected typed request: {exception.Message}"));
            return 2;
        }
    }

    private static void Validate(ElevatedWorkerRequest request)
    {
        if (request.SchemaVersion != SchemaVersion || request.RequestId == Guid.Empty
            || request.Operation == WorkerOperationKind.Unknown || !Enum.IsDefined(request.Operation)
            || request.Changes is not { Count: > 0 and <= 100 })
        {
            throw new InvalidDataException("Worker request envelope is invalid.");
        }

        var addresses = new HashSet<SettingAddress>();
        foreach (var change in request.Changes)
        {
            if (change is null || change.Expected is null || change.Desired is null
                || change.Source == ChangeSource.Unknown || !Enum.IsDefined(change.Source)
                || (!change.Expected.Exists && change.Expected.Value != 0)
                || (!change.Desired.Exists && change.Desired.Value != 0))
            {
                throw new InvalidDataException("Worker setting operation is invalid.");
            }

            var definition = SettingCatalog.Get(change.SettingId);
            if (definition.Evidence == EvidenceLevel.Blocked)
            {
                throw new InvalidOperationException($"{definition.Id} is blocked.");
            }

            var address = definition.ResolveAddress(change.TargetId);
            WindowsRegistrySettingStore.EnsureValidatedForIsolatedVm(address);
            if (!addresses.Add(address))
            {
                throw new InvalidDataException($"Duplicate operation for {definition.Id}.");
            }

            if (change.Expected.Exists) definition.Validate(change.Expected.Value);
            if (change.Desired.Exists) definition.Validate(change.Desired.Value);
        }
    }

    internal static async Task<ApplyResult> ExecuteAsync(
        ElevatedWorkerRequest request,
        ISettingStore store,
        TransactionAuditStore auditStore,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var plan = new ChangePlan(DateTimeOffset.Now, request.Changes.Select(change =>
        {
            var definition = SettingCatalog.Get(change.SettingId);
            return new PlannedChange(
                definition,
                definition.ResolveAddress(change.TargetId),
                new StoredSettingValue(change.Expected.Exists, change.Expected.Value),
                new StoredSettingValue(change.Desired.Exists, change.Desired.Value),
                change.Source);
        }).ToArray());
        var transactions = new SettingTransactionService();
        var result = await transactions.ApplyAsync(plan, store, cancellationToken);
        try
        {
            if (request.Operation == WorkerOperationKind.Apply)
            {
                auditStore.SaveApply(result);
            }
            else
            {
                auditStore.SaveRollback(result.Snapshot, result.Success ? [] : [result.Error ?? "Rollback failed."]);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                          or JsonException or InvalidDataException or ArgumentException
                                          or InvalidOperationException or KeyNotFoundException)
        {
            var compensationErrors = result.Success
                ? await transactions.RollbackAsync(result.Snapshot, store, CancellationToken.None)
                : result.RollbackErrors;
            var compensation = compensationErrors.Count == 0
                ? "The operation was restored to its pre-request state."
                : $"Compensation errors: {string.Join("; ", compensationErrors)}";
            throw new InvalidOperationException($"Audit persistence failed. {compensation}", exception);
        }

        return result;
    }

    private static Task WriteAsync(TextWriter output, ElevatedWorkerResponse response) =>
        output.WriteAsync(JsonSerializer.Serialize(response, Options));
}
