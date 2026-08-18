using System.Security.Cryptography;
using System.Text;
using SockTuner.Models;

namespace SockTuner.Services;

public interface ISettingStore
{
    Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken);
    Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken);
}

public sealed class SettingTransactionService
{
    private static readonly SemaphoreSlim ApplyLock = new(1, 1);
    private readonly Guid _authorityId = Guid.NewGuid();
    private readonly byte[] _authorityKey = RandomNumberGenerator.GetBytes(32);
    private readonly SettingSpecificationResolver _resolve;

    public SettingTransactionService() : this(SettingSpecifications.Live()) { }

    public SettingTransactionService(SettingSpecificationResolver resolve) => _resolve = resolve;

    public async Task<ChangePlan> PrepareAsync(
        IEnumerable<ChangeRequest> requests,
        ISettingStore store,
        CancellationToken cancellationToken)
    {
        var candidates = requests.Select(request =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = _resolve(request.SettingId, request.TargetId);
            if (definition.Evidence == EvidenceLevel.Blocked)
            {
                throw new InvalidOperationException($"{definition.Id} is blocked from change plans.");
            }

            if (request.ProposedValue is not null)
            {
                definition.Validate(request.ProposedValue);
            }
            else if (!definition.SupportsAbsentValue)
            {
                throw new InvalidOperationException(
                    $"{definition.Title} cannot be removed; propose its default value instead.");
            }

            return (Request: request, Definition: definition, Address: definition.ResolveAddress(request.TargetId));
        }).OrderBy(candidate => candidate.Address.RegistryPath, StringComparer.OrdinalIgnoreCase)
          .ThenBy(candidate => candidate.Address.ValueName, StringComparer.OrdinalIgnoreCase)
          .ToArray();
        var changes = new List<PlannedChange>();
        var addresses = new HashSet<SettingAddress>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!addresses.Add(candidate.Address))
            {
                throw new InvalidOperationException($"Duplicate change for {candidate.Request.SettingId}.");
            }

            var before = await store.ReadAsync(candidate.Address, cancellationToken);
            if (before.Exists)
            {
                try
                {
                    candidate.Definition.Validate(before.Value);
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    throw new InvalidOperationException(
                        $"Current value {before.Value} for {candidate.Definition.Title} is outside the supported catalog; change refused.", exception);
                }
            }

            var after = candidate.Request.ProposedValue is { } proposed
                ? new StoredSettingValue(true, proposed)
                : StoredSettingValue.Missing;
            if (before != after)
            {
                changes.Add(new PlannedChange(candidate.Definition, candidate.Address, before, after, candidate.Request.Source));
            }
        }

        return new ChangePlan(DateTimeOffset.Now, changes);
    }

    public async Task<ApplyResult> ApplyAsync(ChangePlan plan, ISettingStore store, CancellationToken cancellationToken)
    {
        var changes = ValidateAndCanonicalize(plan);
        var snapshot = new SettingSnapshot(
            Guid.NewGuid(), _authorityId, Environment.MachineName, DateTimeOffset.Now, changes, false, string.Empty);
        var applied = new List<PlannedChange>();

        await ApplyLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await store.ReadAsync(change.Address, cancellationToken);
                if (current != change.Before)
                {
                    throw new InvalidOperationException($"Stale plan: {change.Definition.Title} changed after preview.");
                }

                applied.Add(change);
                await store.WriteAsync(change.Address, change.After, cancellationToken);
                var verified = await store.ReadAsync(change.Address, cancellationToken);
                if (verified != change.After)
                {
                    throw new InvalidOperationException($"Read-back verification failed for {change.Definition.Title}.");
                }
            }

            var successfulSnapshot = snapshot with { AppliedSuccessfully = true, Signature = string.Empty };
            successfulSnapshot = successfulSnapshot with { Signature = Sign(successfulSnapshot) };
            return new ApplyResult(true, successfulSnapshot, null, []);
        }
        catch (Exception exception)
        {
            var rollbackErrors = await RollBackAppliedAsync(applied, store);
            var error = rollbackErrors.Count == 0
                ? exception.Message
                : $"{exception.Message} Rollback errors: {string.Join("; ", rollbackErrors)}";
            return new ApplyResult(false, snapshot, error, rollbackErrors);
        }
        finally
        {
            ApplyLock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> RollbackAsync(
        SettingSnapshot snapshot,
        ISettingStore store,
        CancellationToken cancellationToken)
    {
        if (!snapshot.AppliedSuccessfully ||
            snapshot.AuthorityId != _authorityId ||
            !string.Equals(snapshot.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
            !HasValidSignature(snapshot))
        {
            return ["Snapshot provenance or integrity does not match this SockTuner session and machine."];
        }

        var changes = ValidateAndCanonicalize(new ChangePlan(snapshot.CreatedAt, snapshot.Changes));
        await ApplyLock.WaitAsync(cancellationToken);
        try
        {
            return await RestoreAsync(changes.Reverse(), store, cancellationToken);
        }
        finally
        {
            ApplyLock.Release();
        }
    }

    private IReadOnlyList<PlannedChange> ValidateAndCanonicalize(ChangePlan plan)
    {
        var validated = new List<PlannedChange>(plan.Changes.Count);
        var addresses = new HashSet<SettingAddress>();
        foreach (var change in plan.Changes)
        {
            var definition = _resolve(change.Address.SettingId, change.Address.TargetId);
            if (definition.Evidence == EvidenceLevel.Blocked)
            {
                throw new InvalidOperationException($"{definition.Id} is blocked from writes.");
            }

            if (!change.After.Exists && !definition.SupportsAbsentValue)
            {
                throw new InvalidOperationException(
                    $"{definition.Title} cannot be removed; propose its default value instead.");
            }

            var expectedAddress = definition.ResolveAddress(change.Address.TargetId);
            if (change.Address != expectedAddress || !addresses.Add(expectedAddress))
            {
                throw new InvalidOperationException($"Invalid or duplicate address for {definition.Id}.");
            }

            if (change.Before.Exists)
            {
                definition.Validate(change.Before.Value);
            }

            if (change.After.Exists)
            {
                definition.Validate(change.After.Value);
            }

            validated.Add(change with { Definition = definition, Address = expectedAddress });
        }

        if (validated.Count == 0)
        {
            throw new InvalidOperationException("An empty change plan cannot be applied.");
        }

        return validated.OrderBy(change => change.Address.RegistryPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(change => change.Address.ValueName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string Sign(SettingSnapshot snapshot)
    {
        var payload = new StringBuilder()
            .Append(snapshot.Id).Append('|')
            .Append(snapshot.AuthorityId).Append('|')
            .Append(snapshot.CreatedAt.UtcTicks).Append('|')
            .Append(snapshot.AppliedSuccessfully);
        Field(snapshot.MachineName);
        foreach (var change in snapshot.Changes)
        {
            Field(change.Address.SettingId);
            Field(change.Address.TargetId);
            payload.Append('|').Append(change.Before.Exists);
            Field(change.Before.Value);
            payload.Append('|').Append(change.After.Exists);
            Field(change.After.Value);
            payload.Append('|').Append(change.Source);
        }

        return Convert.ToBase64String(HMACSHA256.HashData(_authorityKey, Encoding.UTF8.GetBytes(payload.ToString())));

        // Values are now free-form text, so every variable-length field carries its length:
        // without it a value containing the separator could shift the following fields and
        // let two different snapshots produce the same payload.
        void Field(string? value) => payload
            .Append('|')
            .Append(value?.Length ?? -1)
            .Append(':')
            .Append(value);
    }

    private bool HasValidSignature(SettingSnapshot snapshot)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(snapshot.Signature),
                Convert.FromBase64String(Sign(snapshot with { Signature = string.Empty })));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static Task<IReadOnlyList<string>> RollBackAppliedAsync(
        IEnumerable<PlannedChange> applied,
        ISettingStore store) =>
        RestoreAsync(applied.Reverse(), store, CancellationToken.None);

    private static async Task<IReadOnlyList<string>> RestoreAsync(
        IEnumerable<PlannedChange> changes,
        ISettingStore store,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var change in changes)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await store.ReadAsync(change.Address, cancellationToken);
                if (current == change.Before)
                {
                    continue;
                }

                if (current != change.After)
                {
                    errors.Add($"{change.Definition.Title}: current value changed externally; rollback refused.");
                    continue;
                }

                await store.WriteAsync(change.Address, change.Before, cancellationToken);
                if (await store.ReadAsync(change.Address, cancellationToken) != change.Before)
                {
                    errors.Add($"Read-back verification failed for {change.Definition.Title}.");
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{change.Definition.Title}: {exception.Message}");
            }
        }

        return errors;
    }
}
