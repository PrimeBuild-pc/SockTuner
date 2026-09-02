using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Services;

/// <summary>What one setting did when a real write was attempted against it.</summary>
public sealed record DeviceWriteOutcome(
    string SettingId,
    string Target,
    string Before,
    string Proposed,
    bool Applied,
    bool Restored,
    string? Error)
{
    public string Summary =>
        $"{SettingId} on {Target}: {(Applied ? "written and verified" : "refused")}"
        + (Applied ? $", {(Restored ? "restored" : "NOT RESTORED")}" : string.Empty)
        + (Error is null ? string.Empty : $" — {Error}");
}

public sealed record DeviceWriteVerificationReport(
    DateTimeOffset RunAt,
    IReadOnlyList<DeviceWriteOutcome> Outcomes,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Notes,
    string Verdict);

/// <summary>
/// Applies each device-level setting for real and puts it back, so the write path is proven by
/// using it rather than by reasoning about it.
/// </summary>
/// <remarks>
/// <para>
/// Everything goes through snapshot, apply, read-back verification and rollback — the same engine
/// the tuning plan uses. Exercising a different path would prove nothing about the one that ships.
/// </para>
/// <para>
/// Two of the three settings are safe to flip anywhere: a QoS policy only marks packets and takes
/// effect at the next policy refresh, and the power-management DWORD takes effect at the next
/// adapter restart. Disabling an adapter is not safe anywhere, so it is only attempted against an
/// adapter that carries no default route, and never against the one carrying traffic. On a machine
/// with a single NIC that test is skipped and says so, which is a better outcome than a validation
/// run that strands the machine it was validating.
/// </para>
/// </remarks>
public sealed class DeviceWriteVerifier
{
    /// <summary>The policy this run creates and removes. The application never has to exist.</summary>
    public const string CanaryApplication = "socktuner-write-canary.exe";

    private readonly SettingTransactionService _transactions;

    public DeviceWriteVerifier(SettingTransactionService transactions) =>
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));

    public async Task<DeviceWriteVerificationReport> RunAsync(
        IReadOnlyList<AdapterInfo> adapters,
        ISettingStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var outcomes = new List<DeviceWriteOutcome>();
        var skipped = new List<string>();
        var notes = new List<string>();

        // 1. A QoS policy: created, verified, removed. Nothing on the machine depends on it.
        var policyName = QosPolicySpecification.NameFor(CanaryApplication);
        outcomes.Add(await AttemptAsync(
            QosPolicySpecification.SettingId,
            policyName,
            new QosPolicyValue(
                QosPolicySpecification.ExpeditedForwarding, CanaryApplication, "UDP", "50000-50001").Canonical,
            store,
            cancellationToken));

        // Stated rather than discovered: removing a policy leaves the shared container behind, and a
        // reader comparing the registry either side of this run will see it. Saying so here is the
        // difference between a known imperfection and an unexplained one.
        notes.Add(
            $@"Removing a QoS policy leaves the shared HKLM\{QosPolicySpecification.PolicyRoot} container "
            + "in place when it ends up empty. It is created as part of writing the first policy and is "
            + "inert, and this app cannot tell a container it created from one that was already there, so "
            + "it is deliberately not deleted.");

        // 2. Power management on a real adapter. It does not drop the link when written; the driver
        //    picks it up at its next restart, which this run deliberately does not trigger.
        var keys = AdapterPowerSavingSpecification.ReadAdapterKeys();
        // Any adapter with a class key, not only a physical one: the setting is the key, and a VM
        // has no physical NIC at all, which is where this is validated.
        var powerTarget = adapters
            .Where(adapter => Guid.TryParse(adapter.Id, out _))
            .FirstOrDefault(adapter => keys.ContainsKey(Guid.Parse(adapter.Id)));

        if (powerTarget is null)
        {
            skipped.Add("Adapter power management: no adapter with a network class key was found.");
        }
        else
        {
            outcomes.Add(await AttemptAsync(
                AdapterPowerSavingSpecification.SettingId,
                Guid.Parse(powerTarget.Id).ToString(),
                AdapterPowerSavingSpecification.PowerManagementOff.ToString(),
                store,
                cancellationToken));
        }

        // 3. Adapter state, and only on something that is not carrying the session.
        var advice = InterfaceAdvisor.Advise(adapters, singlePathPreferred: false);
        var disposable = advice
            .Where(item => item.Role != InterfaceRole.Carrying && item.Adapter.IsUp)
            .Select(item => item.Adapter)
            .FirstOrDefault(adapter => Guid.TryParse(adapter.Id, out _));

        if (disposable is null)
        {
            skipped.Add(
                "Adapter enable/disable: this machine has no adapter that is up and not carrying the default "
                + "route. Attempting it on the carrying adapter would cut the machine off, so it is not attempted. "
                + "Add a second network adapter to the VM to cover this path.");
        }
        else
        {
            outcomes.Add(await AttemptAsync(
                AdapterStateSpecification.SettingId,
                Guid.Parse(disposable.Id).ToString(),
                AdapterStateSpecification.Disabled,
                store,
                cancellationToken));
        }

        return new DeviceWriteVerificationReport(
            DateTimeOffset.Now, outcomes, skipped, notes, Summarise(outcomes, skipped));
    }

    private async Task<DeviceWriteOutcome> AttemptAsync(
        string settingId,
        string target,
        string proposed,
        ISettingStore store,
        CancellationToken cancellationToken)
    {
        var before = "unknown";
        try
        {
            var plan = await _transactions.PrepareAsync(
                [new ChangeRequest(settingId, target, proposed)], store, cancellationToken);

            if (plan.Changes.Count == 0)
            {
                return new DeviceWriteOutcome(
                    settingId, target, before, proposed, false, true,
                    "The setting already holds the proposed value, so the write path was not exercised.");
            }

            before = plan.Changes[0].BeforeDisplay;
            var result = await _transactions.ApplyAsync(plan, store, cancellationToken);
            if (!result.Success)
            {
                return new DeviceWriteOutcome(settingId, target, before, proposed, false, true, result.Error);
            }

            var rollbackErrors = await _transactions.RollbackAsync(result.Snapshot, store, cancellationToken);
            return new DeviceWriteOutcome(
                settingId, target, before, proposed, true, rollbackErrors.Count == 0,
                rollbackErrors.Count == 0 ? null : string.Join(" ", rollbackErrors));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException
                                          or KeyNotFoundException or UnauthorizedAccessException)
        {
            // A refusal is a result, not a crash: finding out what the platform refuses is the point.
            return new DeviceWriteOutcome(settingId, target, before, proposed, false, true, exception.Message);
        }
    }

    internal static string Summarise(
        IReadOnlyList<DeviceWriteOutcome> outcomes,
        IReadOnlyList<string> skipped)
    {
        var notRestored = outcomes.Where(outcome => !outcome.Restored).ToArray();
        if (notRestored.Length > 0)
        {
            // The headline, not a footnote: a machine left changed by a validation run is the worst
            // outcome this can produce, and it must be the first thing anybody reads.
            return "NOT RESTORED: "
                + string.Join("; ", notRestored.Select(outcome => $"{outcome.SettingId} on {outcome.Target}"))
                + ". Restore this VM from its checkpoint.";
        }

        var applied = outcomes.Count(outcome => outcome.Applied);
        var refused = outcomes.Count - applied;
        return $"{applied} of {outcomes.Count} device setting(s) accepted a real write, were verified by "
            + $"read-back and were restored exactly. {refused} refused."
            + (skipped.Count == 0 ? string.Empty : $" {skipped.Count} not attempted.");
    }
}
