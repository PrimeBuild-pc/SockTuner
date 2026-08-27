using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>What one template did when a real write was attempted against it.</summary>
public sealed record TemplateWriteOutcome(
    string Template,
    string Property,
    string Before,
    string Proposed,
    bool Accepted,
    bool Restored,
    IReadOnlyList<string> Warnings,
    string? Error);

public sealed record TcpWriteVerificationReport(
    DateTimeOffset RunAt,
    string ResolvedTemplate,
    bool ResolvedFromFilter,
    string ResolutionReason,
    IReadOnlyList<TcpTransportFilter> Filters,
    IReadOnlyList<TemplateWriteOutcome> Outcomes,
    string Verdict);

/// <summary>
/// Answers the one question that cannot be answered by reading: which TCP templates actually accept
/// a write, and does the template real traffic uses happen to be one of them?
/// </summary>
/// <remarks>
/// Windows keeps built-in templates alongside Custom ones, and the built-in ones are widely
/// described as read-only — which would mean the only writable template is the one carrying no
/// traffic. That combination has to be discovered, not assumed, so this flips one deliberately
/// harmless property on each template through the real transaction engine and puts it back.
/// Everything goes through snapshot / apply / verify / rollback; nothing bypasses that path,
/// because validating a path you did not use proves nothing about it.
/// </remarks>
public sealed class TcpWriteVerifier
{
    /// <summary>
    /// The canary. It is the lowest-risk entry in the catalog — it only affects peers that do not
    /// support selective acknowledgement, which is close to none — and it is an exact two-value
    /// enum, so the flip and the restore are both unambiguous.
    /// </summary>
    public const string CanaryProperty = "NonSackRttResiliency";

    private readonly SettingTransactionService _transactions;

    public TcpWriteVerifier(SettingTransactionService transactions) => _transactions = transactions;

    public async Task<TcpWriteVerificationReport> RunAsync(
        IReadOnlyList<GlobalSettingCapability> capabilities,
        TcpTemplateResolution resolution,
        ISettingStore store,
        Func<IReadOnlyList<string>> warnings,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<TemplateWriteOutcome>();
        var candidates = capabilities
            .Where(item => string.Equals(item.Property, CanaryProperty, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.InstanceKey is { Length: > 0 })
            .OrderBy(item => item.InstanceKey, StringComparer.OrdinalIgnoreCase);

        foreach (var capability in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcomes.Add(await AttemptAsync(capability, store, warnings, cancellationToken));
        }

        return new TcpWriteVerificationReport(
            DateTimeOffset.Now,
            resolution.Template,
            resolution.FromFilter,
            resolution.Reason,
            resolution.Filters,
            outcomes,
            Summarise(resolution, outcomes));
    }

    private async Task<TemplateWriteOutcome> AttemptAsync(
        GlobalSettingCapability capability,
        ISettingStore store,
        Func<IReadOnlyList<string>> warnings,
        CancellationToken cancellationToken)
    {
        var template = capability.InstanceKey!;
        var before = capability.CurrentValue;
        var proposed = capability.Choices
            .Select(choice => choice.RegistryValue)
            .FirstOrDefault(value => !string.Equals(value, before, StringComparison.Ordinal));
        if (proposed is null)
        {
            return new TemplateWriteOutcome(
                template, CanaryProperty, before, before, false, true, [],
                "The provider advertises only the current value, so there is nothing to flip.");
        }

        var seen = warnings().Count;
        try
        {
            var plan = await _transactions.PrepareAsync(
                [new ChangeRequest(capability.SettingId, template, proposed)], store, cancellationToken);
            var result = await _transactions.ApplyAsync(plan, store, cancellationToken);
            var raised = warnings().Skip(seen).ToArray();
            if (!result.Success)
            {
                return new TemplateWriteOutcome(
                    template, CanaryProperty, before, proposed, false, true, raised, result.Error);
            }

            var rollbackErrors = await _transactions.RollbackAsync(result.Snapshot, store, cancellationToken);
            return new TemplateWriteOutcome(
                template, CanaryProperty, before, proposed, true, rollbackErrors.Count == 0, raised,
                rollbackErrors.Count == 0 ? null : string.Join(" ", rollbackErrors));
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentException or UnauthorizedAccessException)
        {
            // A refusal is a result, not a crash: it is exactly what this run is here to find out.
            return new TemplateWriteOutcome(
                template, CanaryProperty, before, proposed, false, true,
                warnings().Skip(seen).ToArray(), exception.Message);
        }
    }

    internal static string Summarise(TcpTemplateResolution resolution, IReadOnlyList<TemplateWriteOutcome> outcomes)
    {
        var accepted = outcomes.Where(outcome => outcome.Accepted).Select(outcome => outcome.Template).ToArray();
        var refused = outcomes.Where(outcome => !outcome.Accepted).Select(outcome => outcome.Template).ToArray();
        var notRestored = outcomes.Where(outcome => !outcome.Restored).Select(outcome => outcome.Template).ToArray();

        if (notRestored.Length > 0)
        {
            return $"ROLLBACK FAILED on {string.Join(", ", notRestored)}. The machine was left changed; restore it before "
                + "drawing any other conclusion from this run.";
        }

        if (accepted.Length == 0)
        {
            return $"No template accepted a write ({string.Join(", ", refused)} all refused). The global TCP surface is "
                + "not writable through this provider on this build, and the catalog should say so rather than offer it.";
        }

        var targetWritable = accepted.Contains(resolution.Template, StringComparer.OrdinalIgnoreCase);
        if (targetWritable)
        {
            return $"The template carrying traffic ({resolution.Template}) accepts writes and rolled back exactly. "
                + $"Accepted: {string.Join(", ", accepted)}."
                + (refused.Length > 0 ? $" Refused: {string.Join(", ", refused)}." : string.Empty);
        }

        return $"The template carrying traffic ({resolution.Template}) REFUSED the write, while "
            + $"{string.Join(", ", accepted)} accepted it. Writing a setting therefore needs a second step — pointing a "
            + "transport filter at a writable template — and SockTuner must not offer these settings until it does that.";
    }
}
