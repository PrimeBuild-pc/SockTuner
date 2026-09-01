using System.IO;
using System.Globalization;
using Microsoft.Win32;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// A machine-wide QoS policy that marks one application's packets with a DSCP value, as a typed
/// setting the transaction engine can snapshot, apply, verify and roll back.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that makes the router advice usable. SockTuner's shaping guidance says to use
/// <c>piece_of_cake.qos</c> and to move to <c>layer_cake.qos</c> only if traffic is actually
/// DSCP-marked — and until now nothing in the app could mark it. A policy here writes the mark; the
/// router decides what to do with it.
/// </para>
/// <para>
/// What it cannot do is worth stating plainly, because "QoS" is sold as a magic word. A DSCP mark
/// is a request, not a reservation. It is honoured by equipment on the path that is configured to
/// honour it — typically your own router, and only if you set it up — and most consumer ISPs
/// rewrite or ignore the field at the access link. Marking traffic changes nothing at all by
/// itself. It only becomes useful once something downstream is looking.
/// </para>
/// <para>
/// The policy lives in the documented Group Policy location. The whole key is written together or
/// removed together, exactly like the interrupt affinity override: a policy with half its values is
/// not a state the Group Policy editor can produce, and absent is the real "no policy" state, which
/// is what a rollback has to restore.
/// </para>
/// <para>
/// SockTuner only manages policies it created. Every name it accepts carries a fixed prefix, so a
/// plan cannot be pointed at a policy an administrator deployed and delete it.
/// </para>
/// </remarks>
public sealed class QosPolicySpecification : ISettingSpecification
{
    public const string SettingId = "qos.policy";

    /// <summary>The documented machine-wide QoS policy store.</summary>
    public const string PolicyRoot = @"SOFTWARE\Policies\Microsoft\Windows\QoS";

    /// <summary>
    /// Every policy this app writes carries this prefix, and it refuses to touch anything without
    /// it. That is what stops a plan from naming — and removing — a policy somebody else deployed.
    /// </summary>
    public const string NamePrefix = "SockTuner - ";

    /// <summary>Expedited Forwarding. The conventional mark for interactive real-time traffic.</summary>
    public const int ExpeditedForwarding = 46;

    private const int MaximumNameLength = 64;

    public string Id => SettingId;
    public string Title => "QoS policy";
    public string Category => "Quality of service";

    // Documented by Microsoft as the policy-based QoS registry format, and it is what the
    // MSFT_NetQosPolicySettingData provider this app already reads reports back.
    public EvidenceLevel Evidence => EvidenceLevel.Documented;

    // Marking a packet cannot sever anything: the risk is that it does nothing, not that it breaks
    // something. Medium rather than Low because a wrong port range can mark the wrong traffic.
    public ChangeRisk Risk => ChangeRisk.Medium;

    // A machine policy is picked up on the next policy refresh; a reboot is the reliable point.
    public string RestartRequirement => "System reboot";

    public string TradeOff =>
        "A DSCP mark is a request, not a reservation. It does nothing unless something on the path is "
        + "configured to honour it — your own router with a DSCP-aware queue discipline, in practice — and "
        + "most consumer ISPs rewrite or ignore the field beyond your access link. Marked traffic can also "
        + "be de-prioritised by equipment that treats an unexpected mark as suspect. Mark deliberately, on "
        + "one application, and verify with a measurement rather than assuming.";

    /// <summary>Absent is "no such policy", which is what removing it has to restore.</summary>
    public bool SupportsAbsentValue => true;

    public void Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var policy = QosPolicyValue.Parse(value);

        if (policy.Dscp is < 0 or > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A DSCP value is 0 to 63.");
        }

        if (policy.Application.Length == 0 || policy.Application.Length > 260)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), "A policy needs an application to match, such as game.exe.");
        }

        // An application match is a file name or a full path; either way it must not smuggle a
        // separator that would change how the value is parsed back.
        if (policy.Application.Contains(';', StringComparison.Ordinal)
            || policy.Application.Contains('=', StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "An application match cannot contain ';' or '='.");
        }

        if (policy.Protocol is not ("TCP" or "UDP" or "*"))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Protocol is TCP, UDP or *.");
        }

        ValidatePorts(policy.RemotePorts);

        if (!string.Equals(policy.Canonical, value, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value is not in canonical form.");
        }
    }

    private static void ValidatePorts(string ports)
    {
        if (ports == "*") return;

        var parts = ports.Split('-');
        if (parts.Length is < 1 or > 2
            || !parts.All(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                && port is >= 1 and <= 65535))
        {
            throw new ArgumentOutOfRangeException(nameof(ports), $"'{ports}' is not a port or a port range.");
        }

        if (parts.Length == 2
            && int.Parse(parts[0], CultureInfo.InvariantCulture) > int.Parse(parts[1], CultureInfo.InvariantCulture))
        {
            throw new ArgumentOutOfRangeException(nameof(ports), "A port range must not end before it starts.");
        }
    }

    public SettingAddress ResolveAddress(string? targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new ArgumentException("A QoS policy needs a name.", nameof(targetId));
        }

        if (!targetId.StartsWith(NamePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"SockTuner only manages policies named \"{NamePrefix}…\", so it cannot touch \"{targetId}\".",
                nameof(targetId));
        }

        if (targetId.Length > MaximumNameLength)
        {
            throw new ArgumentException($"A policy name is at most {MaximumNameLength} characters.", nameof(targetId));
        }

        // The name becomes a registry subkey, so it is restricted to characters that cannot
        // traverse or terminate the path rather than merely escaped.
        if (!targetId.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is ' ' or '-' or '_' or '.'))
        {
            throw new ArgumentException(
                "A policy name may hold letters, digits, spaces, hyphens, underscores and dots only.",
                nameof(targetId));
        }

        return new SettingAddress(SettingId, targetId, $@"{PolicyRoot}\{targetId}", "DSCP Value", RegistryValueKind.String);
    }

    /// <summary>A conventional name for a policy that marks one executable.</summary>
    public static string NameFor(string application) =>
        NamePrefix + new string([.. Path.GetFileNameWithoutExtension(application)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is ' ' or '-' or '_')])
            .Trim();
}

/// <summary>The four fields a SockTuner policy carries, as one canonical value.</summary>
public sealed record QosPolicyValue(int Dscp, string Application, string Protocol, string RemotePorts)
{
    public string Canonical =>
        $"dscp={Dscp};app={Application};protocol={Protocol};remote={RemotePorts}";

    public static QosPolicyValue Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // First occurrence wins, deliberately. A repeated field would otherwise throw a duplicate-key
        // exception, and rejecting a crafted value by accident is not the same as rejecting it on
        // purpose: with first-wins the value simply fails the canonical-form check below, which is
        // the check that exists to catch exactly this.
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in value.Split(';').Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2))
        {
            fields.TryAdd(pair[0], pair[1]);
        }

        if (!fields.TryGetValue("dscp", out var dscp)
            || !int.TryParse(dscp, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var mark))
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"'{value}' has no dscp field.");
        }

        return new(
            mark,
            fields.GetValueOrDefault("app", string.Empty),
            fields.GetValueOrDefault("protocol", "*"),
            fields.GetValueOrDefault("remote", "*"));
    }
}

/// <summary>
/// Writes and removes a QoS policy as a whole key, in the documented Group Policy format.
/// </summary>
public sealed class QosPolicyStore : ISettingStore
{
    private readonly QosPolicySpecification _specification;

    public QosPolicyStore(QosPolicySpecification specification) =>
        _specification = specification ?? throw new ArgumentNullException(nameof(specification));

    public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwned(address);

        using var key = Registry.LocalMachine.OpenSubKey(address.RegistryPath, writable: false);
        if (key?.GetValue("DSCP Value") is not string dscp
            || !int.TryParse(dscp, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var mark))
        {
            return Task.FromResult(StoredSettingValue.Missing);
        }

        return Task.FromResult(new StoredSettingValue(true, new QosPolicyValue(
            mark,
            key.GetValue("Application Name") as string ?? string.Empty,
            key.GetValue("Protocol") as string ?? "*",
            PortsFrom(key)).Canonical));
    }

    public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwned(address);

        // Re-resolve inside the writing process: the plan chooses which policy name, and this
        // decides — and re-checks — which key that is.
        if (_specification.ResolveAddress(address.TargetId) != address)
        {
            throw new InvalidOperationException("The QoS policy address does not match the resolved policy name.");
        }

        if (!value.Exists)
        {
            // Removing the key, not blanking its values: a policy left behind with empty fields is
            // still a policy to everything that enumerates this hive.
            using var root = Registry.LocalMachine.OpenSubKey(QosPolicySpecification.PolicyRoot, writable: true);
            root?.DeleteSubKeyTree(address.TargetId!, throwOnMissingSubKey: false);
            return Task.CompletedTask;
        }

        _specification.Validate(value.Value);
        var policy = QosPolicyValue.Parse(value.Value);

        using var key = Registry.LocalMachine.CreateSubKey(address.RegistryPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open HKLM\\{address.RegistryPath}");

        // Every value is REG_SZ in this format, including the numeric ones.
        key.SetValue("Version", "1.0", RegistryValueKind.String);
        key.SetValue("Application Name", policy.Application, RegistryValueKind.String);
        key.SetValue("Protocol", policy.Protocol, RegistryValueKind.String);
        key.SetValue("Local Port", "*", RegistryValueKind.String);
        key.SetValue("Remote Port", policy.RemotePorts, RegistryValueKind.String);
        key.SetValue("Local IP", "*", RegistryValueKind.String);
        key.SetValue("Remote IP", "*", RegistryValueKind.String);
        key.SetValue("DSCP Value", policy.Dscp.ToString(CultureInfo.InvariantCulture), RegistryValueKind.String);

        // -1 is the documented "do not throttle". A policy that marks traffic must not also start
        // rate-limiting it because a field was left unset.
        key.SetValue("Throttle Rate", "-1", RegistryValueKind.String);
        return Task.CompletedTask;
    }

    private static string PortsFrom(RegistryKey key) => key.GetValue("Remote Port") as string ?? "*";

    private static void EnsureOwned(SettingAddress address)
    {
        if (!string.Equals(address.SettingId, QosPolicySpecification.SettingId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{address.SettingId} is not a QoS policy setting.");
        }
    }
}
