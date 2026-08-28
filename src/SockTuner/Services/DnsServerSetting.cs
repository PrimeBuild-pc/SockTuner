using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// The static DNS server list on one interface, as a typed setting the transaction engine can
/// snapshot, apply, verify and roll back like any other.
/// </summary>
/// <remarks>
/// An absent value is meaningful here and is not "unset": it means the interface takes its
/// resolvers from DHCP. Rolling back to DHCP is therefore a real operation rather than a deletion,
/// which is why <see cref="SupportsAbsentValue"/> is true.
/// </remarks>
public sealed class DnsServerSpecification : ISettingSpecification
{
    public const string SettingId = "dns.servers";
    public const int MaximumServers = 3;
    private const string InterfacesPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

    public string Id => SettingId;
    public string Title => "DNS servers";
    public string Category => "Name resolution";
    public EvidenceLevel Evidence => EvidenceLevel.Documented;
    public ChangeRisk Risk => ChangeRisk.Medium;

    // Applied through the supported API, which takes effect without restarting the adapter. The
    // resolver cache is flushed so the next lookup cannot be answered from the old server's answers.
    public string RestartRequirement => "None";

    public string TradeOff =>
        "A resolver decides which answers you get, not only how fast they arrive: filtering, logging and who operates it "
        + "are part of the choice. Changing it shortens the pause before a connection starts and does not affect the "
        + "latency of a session already connected.";

    /// <summary>Absent means the interface uses DHCP-assigned resolvers, which is a state worth restoring exactly.</summary>
    public bool SupportsAbsentValue => true;

    public void Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var servers = Parse(value);
        if (servers.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "At least one resolver address is required.");
        }

        if (servers.Count > MaximumServers)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), $"Windows uses at most {MaximumServers} static resolvers per interface.");
        }

        if (!string.Equals(Canonical(servers), value, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), "Resolver list must be canonical: comma separated, no spaces, no duplicates.");
        }
    }

    public SettingAddress ResolveAddress(string? targetId)
    {
        if (!Guid.TryParse(targetId, out var adapter))
        {
            throw new ArgumentException("A valid adapter GUID is required.", nameof(targetId));
        }

        var normalized = adapter.ToString("B").ToUpperInvariant();
        return new SettingAddress(
            SettingId, normalized, $"{InterfacesPath}\\{normalized}", "NameServer", RegistryValueKind.String);
    }

    /// <summary>Splits a stored list. Windows accepts comma or space separators; both are read.</summary>
    public static IReadOnlyList<string> Parse(string? value) => (value ?? string.Empty)
        .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(part => IPAddress.TryParse(part, out _))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>The one spelling this setting round-trips through, so read-back stays an exact match.</summary>
    public static string Canonical(IEnumerable<string> servers) => string.Join(",", servers);
}

/// <summary>
/// Reads the interface resolver list from its documented registry location and applies changes
/// through <c>SetInterfaceDnsSettings</c>, the supported API, rather than by writing the value and
/// hoping the stack notices.
/// </summary>
public sealed class DnsServerStore : ISettingStore
{
    private readonly DnsServerSpecification _specification = new();
    private readonly Func<Guid, string?, bool>? _apply;

    public DnsServerStore() { }

    internal DnsServerStore(Func<Guid, string?, bool> apply) => _apply = apply;

    public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwned(address);
        using var key = Registry.LocalMachine.OpenSubKey(address.RegistryPath, writable: false);
        var raw = key?.GetValue(address.ValueName) as string;
        var servers = DnsServerSpecification.Parse(raw);

        // An empty or missing value is the DHCP state, reported as absent rather than as an empty list.
        return Task.FromResult(servers.Count == 0
            ? StoredSettingValue.Missing
            : new StoredSettingValue(true, DnsServerSpecification.Canonical(servers)));
    }

    public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwned(address);
        if (!Guid.TryParse(address.TargetId, out var adapter))
        {
            throw new InvalidOperationException("The DNS setting requires a resolved adapter GUID.");
        }

        if (value.Exists)
        {
            _specification.Validate(value.Value);
        }

        // Null restores DHCP; a list sets those resolvers. Both go through the same supported call.
        var applied = (_apply ?? ApplyThroughWindows)(adapter, value.Exists ? value.Value : null);
        if (!applied)
        {
            throw new InvalidOperationException(
                $"Windows refused the resolver change for adapter {adapter}.");
        }

        return Task.CompletedTask;
    }

    private static void EnsureOwned(SettingAddress address)
    {
        if (!string.Equals(address.SettingId, DnsServerSpecification.SettingId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{address.SettingId} is not a DNS server setting.");
        }

        if (address.ValueKind != RegistryValueKind.String)
        {
            throw new InvalidOperationException("The DNS server list is a string value.");
        }
    }

    private static bool ApplyThroughWindows(Guid adapter, string? servers)
    {
        var settings = new DnsInterfaceSettings
        {
            Version = 1,
            Flags = DnsSettingNameServer,
            NameServer = servers
        };

        var result = SetInterfaceDnsSettings(adapter, ref settings);
        if (result != 0)
        {
            return false;
        }

        // Answers already cached came from the previous resolver, so they are dropped rather than
        // left to expire on their own TTL.
        DnsFlushResolverCache();
        return true;
    }

    private const ulong DnsSettingNameServer = 0x08;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DnsInterfaceSettings
    {
        public uint Version;
        public ulong Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Domain;
        [MarshalAs(UnmanagedType.LPWStr)] public string? NameServer;
        [MarshalAs(UnmanagedType.LPWStr)] public string? SearchList;
        public uint RegistrationEnabled;
        public uint RegisterAdapterName;
        public uint EnableLlmnr;
        public uint QueryAdapterName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProfileNameServer;
    }

    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint SetInterfaceDnsSettings(Guid interfaceGuid, ref DnsInterfaceSettings settings);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    private static extern bool DnsFlushResolverCache();
}
