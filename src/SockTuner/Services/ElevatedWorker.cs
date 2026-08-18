using System.IO;
using System.Text;
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
    [property: JsonRequired] string Value);

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
        TransactionAuditStore? auditStore = null,
        SettingSpecificationResolver? resolve = null)
    {
        ElevatedWorkerRequest? request = null;
        try
        {
            // Capabilities are read here, inside the elevated process, immediately before the
            // write: the caller's view of what the driver allows is never trusted.
            resolve ??= SettingSpecifications.Live();
            request = JsonSerializer.Deserialize<ElevatedWorkerRequest>(
                await ReadRequestAsync(input, cancellationToken), Options)
                ?? throw new InvalidDataException("Worker request is empty.");
            Validate(request, resolve);
            var effectiveStore = store ?? CreateDefaultStore();
            var result = await ExecuteAsync(
                request,
                effectiveStore,
                auditStore ?? new TransactionAuditStore(),
                cancellationToken,
                resolve);
            var status = result.Success
                ? "Typed operation applied and verified." + await RestartAsync(effectiveStore, cancellationToken)
                : result.Error ?? "Typed operation failed.";
            await WriteAsync(output, new(SchemaVersion, request.RequestId, result.Success, status));
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

    private static ISettingStore CreateDefaultStore() =>
        new CompositeSettingStore(WindowsRegistrySettingStore.CreateWritable(), new CimAdapterSettingStore());

    // NIC properties only take effect once the miniport restarts, so the adapters that were
    // written to are restarted here and checked back to the link state they had before.
    private static async Task<string> RestartAsync(ISettingStore store, CancellationToken cancellationToken)
    {
        if (store is not CompositeSettingStore { Adapters.TouchedAdapters.Count: > 0 } composite)
        {
            return string.Empty;
        }

        var count = composite.Adapters.TouchedAdapters.Count;
        var problems = await composite.Adapters.RestartTouchedAdaptersAsync(cancellationToken);
        return problems.Count == 0
            ? $" Restarted {count} adapter(s); each returned to its previous link state."
            : $" Restart warnings: {string.Join("; ", problems)}";
    }

    // Requests are newline-delimited so a single connection carries exactly one message, and
    // the cap is enforced while reading rather than after buffering an unbounded payload.
    private static async Task<string> ReadRequestAsync(TextReader input, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[1];
        while (await input.ReadAsync(buffer, cancellationToken) == 1 && buffer[0] != '\n')
        {
            if (builder.Length == MaximumRequestCharacters)
            {
                throw new InvalidDataException("Worker request exceeds the size limit.");
            }

            if (buffer[0] != '\r')
            {
                builder.Append(buffer[0]);
            }
        }

        return builder.Length == 0
            ? throw new InvalidDataException("Worker request is empty.")
            : builder.ToString();
    }

    private static void Validate(ElevatedWorkerRequest request, SettingSpecificationResolver resolve)
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
                || change.Expected.Value is null || change.Desired.Value is null
                || (!change.Expected.Exists && change.Expected.Value.Length != 0)
                || (!change.Desired.Exists && change.Desired.Value.Length != 0))
            {
                throw new InvalidDataException("Worker setting operation is invalid.");
            }

            // Resolving re-reads the driver: a keyword this adapter no longer advertises, or an
            // unknown catalog ID, fails here before anything is written.
            var definition = resolve(change.SettingId, change.TargetId);
            if (definition.Evidence == EvidenceLevel.Blocked)
            {
                throw new InvalidOperationException($"{definition.Id} is blocked.");
            }

            var address = definition.ResolveAddress(change.TargetId);
            if (definition is not NicSettingSpecification)
            {
                WindowsRegistrySettingStore.EnsureWritable(address);
            }

            if (!addresses.Add(address))
            {
                throw new InvalidDataException($"Duplicate operation for {definition.Id}.");
            }

            if (change.Expected.Exists) definition.Validate(change.Expected.Value);
            if (change.Desired.Exists) definition.Validate(change.Desired.Value);
            else if (!definition.SupportsAbsentValue)
            {
                throw new InvalidDataException($"{definition.Id} cannot be removed.");
            }
        }
    }

    internal static async Task<ApplyResult> ExecuteAsync(
        ElevatedWorkerRequest request,
        ISettingStore store,
        TransactionAuditStore auditStore,
        CancellationToken cancellationToken,
        SettingSpecificationResolver? resolve = null)
    {
        resolve ??= SettingSpecifications.Live();
        Validate(request, resolve);
        var plan = new ChangePlan(DateTimeOffset.Now, request.Changes.Select(change =>
        {
            var definition = resolve(change.SettingId, change.TargetId);
            return new PlannedChange(
                definition,
                definition.ResolveAddress(change.TargetId),
                new StoredSettingValue(change.Expected.Exists, change.Expected.Value),
                new StoredSettingValue(change.Desired.Exists, change.Desired.Value),
                change.Source);
        }).ToArray());
        var transactions = new SettingTransactionService(resolve);
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

    // Newline-terminated to match the request framing, so the caller can read exactly one
    // response from a pipe that stays open.
    private static Task WriteAsync(TextWriter output, ElevatedWorkerResponse response) =>
        output.WriteLineAsync(JsonSerializer.Serialize(response, Options));
}
