using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

/// <summary>
/// Whether Windows is being driven from somewhere else, and what that means for a change that
/// interrupts the network.
/// </summary>
/// <remarks>
/// <para>
/// Every other refusal in this app protects the setting. This one protects the way back in. A NIC
/// property restarts its adapter, and an adapter restart on the interface carrying a remote session
/// ends the session — leaving a half-applied plan on a machine nobody can reach to roll it back.
/// The transaction engine survives that correctly, but only if somebody can log in again.
/// </para>
/// <para>
/// The check deliberately over-warns. SockTuner does not work out which adapter carries the session,
/// so it treats every disruptive change as capable of ending it and says exactly that rather than
/// implying it knows. Being wrong in this direction costs a sentence; being wrong in the other
/// direction costs the machine.
/// </para>
/// </remarks>
public static class RemoteSessionGuard
{
    /// <summary>Documented metric: non-zero when the calling process runs in a remote session.</summary>
    private const int SmRemoteSession = 0x1000;

    /// <summary>Overridable for tests; production reads the real metric.</summary>
    internal static Func<bool> IsRemoteSession { get; set; } = () => GetSystemMetrics(SmRemoteSession) != 0;

    /// <summary>The one restart requirement that drops the link while the change is being applied.</summary>
    public const string AdapterRestart = "Adapter restart";

    /// <summary>
    /// Every restart requirement the app produces, and whether it interrupts the network at apply
    /// time. "System reboot" does not: the change is written now and takes effect later, so the
    /// session survives the apply. A test fails if a requirement appears that is not listed here,
    /// so a new one has to be classified deliberately rather than defaulting to harmless.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> RestartRequirements { get; } =
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["None"] = false,
            [AdapterRestart] = true,
            ["Service restart"] = false,
            ["System reboot"] = false
        };

    /// <summary>Whether applying this change drops the link while it happens.</summary>
    public static bool Disrupts(ISettingSpecification definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // An unrecognised requirement is treated as disruptive. The safe default here is to warn.
        return !RestartRequirements.TryGetValue(definition.RestartRequirement, out var interrupts) || interrupts;
    }

    /// <summary>The warning for a prepared plan, or null when there is nothing to say.</summary>
    public static string? WarningFor(IEnumerable<PlannedChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return WarningFor(changes.Count(change => Disrupts(change.Definition)));
    }

    /// <summary>
    /// The warning to show before applying, or null when there is nothing to say. Null is the
    /// normal case: on a local session a link drop is an inconvenience, not a lockout.
    /// </summary>
    /// <param name="disruptiveChanges">
    /// How many changes in the plan restart an adapter or otherwise interrupt connectivity.
    /// </param>
    public static string? WarningFor(int disruptiveChanges)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(disruptiveChanges);
        if (disruptiveChanges == 0 || !IsRemoteSession())
        {
            return null;
        }

        return $"You are working over a remote session, and {disruptiveChanges} change(s) in this plan interrupt the "
            + "network. SockTuner does not know which adapter carries your session, so it has to assume this one might. "
            + "If it does, the connection ends mid-apply and you will need physical or out-of-band access to the machine "
            + "to finish or roll back. Apply from the console, or from an adapter you are certain is not carrying this "
            + "session.";
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
