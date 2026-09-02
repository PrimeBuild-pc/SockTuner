using SockTuner.Models;
using SockTuner.Persistence;

namespace SockTuner.Services.Diagnosis;

/// <summary>What happened to a setting since SockTuner last wrote it.</summary>
public enum DriftState
{
    /// <summary>Still holding the value that was written.</summary>
    Holding,

    /// <summary>Something other than SockTuner changed it.</summary>
    Drifted,

    /// <summary>The setting could not be read back at all.</summary>
    Unreadable
}

/// <summary>One setting SockTuner wrote, and what it holds now.</summary>
public sealed record SettingDrift(
    string SettingId,
    string? TargetId,
    DateTimeOffset WrittenAt,
    StoredSettingValue Expected,
    StoredSettingValue? Actual,
    DriftState State,
    string? Error = null)
{
    public string StateDisplay => State switch
    {
        DriftState.Holding => "Holding",
        DriftState.Drifted => "Changed elsewhere",
        _ => "Could not read"
    };

    public string StateBadge => $"{State switch
    {
        DriftState.Holding => Badges.Good,
        DriftState.Drifted => Badges.Middling,
        _ => Badges.Question
    }} {StateDisplay}";

    public string ExpectedDisplay => Expected.Exists ? Expected.Value : "Removed";
    public string ActualDisplay => Actual is not { } actual ? "—" : actual.Exists ? actual.Value : "Removed";
    public string WrittenAtDisplay => WrittenAt.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// Compares what SockTuner last wrote against what the machine holds now.
/// </summary>
/// <remarks>
/// <para>
/// A driver update reinstalls the INF and puts every advanced property back to its default. So does
/// a "network reset", a vendor utility, and a second tuning tool. None of them tell anyone, and the
/// result is a user who believes a setting is applied because they applied it once, and an app that
/// agrees with them because its audit says so. The audit records what was written; only a read-back
/// says what is there.
/// </para>
/// <para>
/// The expectation is the most recent successful entry per setting and target, whether that was an
/// apply or a rollback. A rollback is SockTuner putting a value back deliberately, so the value it
/// restored — not the value the earlier apply wrote — is what should still be there. Failed
/// transactions are ignored entirely: they did not establish an expectation.
/// </para>
/// </remarks>
public static class SettingDriftAnalyzer
{
    /// <summary>Reads a setting's current value, or throws if it cannot be read.</summary>
    public delegate StoredSettingValue Read(string settingId, string? targetId);

    public static IReadOnlyList<SettingDrift> Compare(
        IReadOnlyList<TransactionAuditEntry> audit,
        Read read)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(read);

        var results = new List<SettingDrift>();
        foreach (var (key, expected) in LatestWrites(audit))
        {
            var (settingId, targetId) = key;
            try
            {
                var actual = read(settingId, targetId);
                results.Add(new SettingDrift(
                    settingId, targetId, expected.At,
                    expected.Value, actual,
                    Matches(expected.Value, actual) ? DriftState.Holding : DriftState.Drifted));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or KeyNotFoundException or NotSupportedException
                                              or UnauthorizedAccessException or System.IO.IOException
                                              or System.Management.ManagementException
                                              or System.Runtime.InteropServices.COMException)
            {
                // A keyword the driver no longer advertises reads as an error rather than as a
                // value. That is itself worth reporting: the setting is gone, not merely different.
                results.Add(new SettingDrift(
                    settingId, targetId, expected.At, expected.Value, null,
                    DriftState.Unreadable, exception.Message));
            }
        }

        return [.. results.OrderBy(item => item.State == DriftState.Holding).ThenBy(item => item.SettingId, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The value SockTuner most recently established for each setting on each target, from the
    /// transactions that actually succeeded.
    /// </summary>
    private static Dictionary<(string SettingId, string? TargetId), (StoredSettingValue Value, DateTimeOffset At)>
        LatestWrites(IReadOnlyList<TransactionAuditEntry> audit)
    {
        var latest = new Dictionary<(string, string?), (StoredSettingValue, DateTimeOffset)>();
        foreach (var entry in audit
                     .Where(item => item.Outcome is TransactionAuditOutcome.ApplySucceeded
                         or TransactionAuditOutcome.RollbackSucceeded)
                     .OrderBy(item => item.RecordedAt))
        {
            foreach (var change in entry.Changes)
            {
                // A rollback entry stores the original plan, unreversed: SaveRollback persists the
                // apply's own snapshot, and RestoreAsync writes each change's Before. So after a
                // successful rollback the machine holds Before, not After. Reading After for both
                // outcomes inverted every verdict that followed a rollback — it called the restored
                // value drift and called the undone value "holding".
                var established = entry.Outcome == TransactionAuditOutcome.RollbackSucceeded
                    ? change.Before
                    : change.After;

                latest[(change.SettingId, change.TargetId)] =
                    (new StoredSettingValue(established.Exists, established.Value), entry.RecordedAt);
            }
        }

        return latest.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    /// <summary>
    /// Absent and present are different states, so a missing value never matches a written one.
    /// Present values compare ordinally: an NDIS keyword is REG_SZ and "1" is not "01".
    /// </summary>
    private static bool Matches(StoredSettingValue expected, StoredSettingValue actual) =>
        expected.Exists == actual.Exists
        && (!expected.Exists || string.Equals(expected.Value, actual.Value, StringComparison.Ordinal));

    /// <summary>A sentence for the surface that shows the result.</summary>
    public static string Summarise(IReadOnlyList<SettingDrift> drift)
    {
        ArgumentNullException.ThrowIfNull(drift);
        if (drift.Count == 0)
        {
            return "SockTuner has not applied anything on this machine yet, so there is nothing to check against.";
        }

        var drifted = drift.Count(item => item.State == DriftState.Drifted);
        var unreadable = drift.Count(item => item.State == DriftState.Unreadable);
        if (drifted == 0 && unreadable == 0)
        {
            return $"All {drift.Count} setting(s) SockTuner wrote still hold the value it wrote.";
        }

        return $"{drift.Count} setting(s) checked: {drifted} changed outside SockTuner, "
            + $"{unreadable} no longer readable. A driver update, a network reset or another tuning tool "
            + "will do this silently, which is why applying once is not the same as still being applied.";
    }
}
