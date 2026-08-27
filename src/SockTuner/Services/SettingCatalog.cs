using Microsoft.Win32;
using SockTuner.Models;

namespace SockTuner.Services;

public static class SettingCatalog
{
    private const string TcpInterfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string MultimediaProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new(
            "tcp.interface.no-delay",
            "TCPNoDelay",
            "TCP ACK behavior",
            SettingScope.AdapterInterface,
            EvidenceLevel.Experimental,
            ChangeRisk.Medium,
            "System reboot",
            "Requests immediate transmission for eligible TCP traffic on one selected interface.",
            "TCP-only; many games use UDP. May increase packet and CPU overhead.",
            TcpInterfaces,
            "TCPNoDelay",
            RegistryValueKind.DWord,
            0,
            1,
            new HashSet<uint> { 0, 1 },
            EvidenceNote: "Unverified. Widely repeated as a per-interface companion to TcpAckFrequency, but no Microsoft documentation and no confirmed consuming binary have been established for this exact value name and path. Kept Experimental until a system-binary check confirms it; the value name is case-sensitive, so a search for \"TcpNoDelay\" does not test \"TCPNoDelay\"."),
        new(
            "tcp.interface.ack-frequency",
            "TcpAckFrequency",
            "TCP ACK behavior",
            SettingScope.AdapterInterface,
            EvidenceLevel.Experimental,
            ChangeRisk.Medium,
            "System reboot",
            "Changes delayed-ACK frequency for one selected TCP/IP interface.",
            "Can increase ACK traffic and is not a UDP latency control.",
            TcpInterfaces,
            "TcpAckFrequency",
            RegistryValueKind.DWord,
            1,
            2,
            new HashSet<uint> { 1, 2 },
            EvidenceNote: "The value name is present in tcpipreg.sys and reached through a kernel registry table whose destination variable is read by driver code, so the value is genuinely consumed. In tcpip.sys the same name populates a variable nothing reads. This establishes that the setting is read, not that it lowers latency, so the level stays Experimental."),
        new(
            "tcp.interface.delayed-ack-ticks",
            "TcpDelAckTicks",
            "TCP ACK behavior",
            SettingScope.AdapterInterface,
            EvidenceLevel.Experimental,
            ChangeRisk.High,
            "System reboot",
            "Changes the delayed-ACK timer multiplier for one selected interface.",
            "Undocumented behavior can vary by Windows build; benchmark and rollback are mandatory.",
            TcpInterfaces,
            "TcpDelAckTicks",
            RegistryValueKind.DWord,
            0,
            6,
            EvidenceNote: "Unverified. Undocumented by Microsoft and not yet checked against any Windows system binary. Highest-priority candidate for a system-binary evidence check; until then it stays Experimental and High risk."),
        new(
            "mmcss.network-throttling-index",
            "NetworkThrottlingIndex",
            "Multimedia scheduler",
            SettingScope.System,
            EvidenceLevel.Documented,
            ChangeRisk.Medium,
            "System reboot",
            "Controls the multimedia scheduler's network throttling index.",
            "Disabling throttling can increase resource contention; it is not universally faster.",
            MultimediaProfile,
            "NetworkThrottlingIndex",
            RegistryValueKind.DWord,
            1,
            uint.MaxValue,
            new HashSet<uint>(Enumerable.Range(1, 70).Select(value => (uint)value).Append(uint.MaxValue)),
            EvidenceNote: "Documented by Microsoft as part of the multimedia class scheduler profile, and the value name is present in mmcss.sys. A binary scan that only walks the .text section reports no reference because the string sits in an init-time section, which is a limitation of that technique rather than evidence against the setting."),
        new(
            "mmcss.system-responsiveness",
            "SystemResponsiveness",
            "Multimedia scheduler",
            SettingScope.System,
            EvidenceLevel.Documented,
            ChangeRisk.Medium,
            "System reboot",
            "Controls the CPU percentage reserved for low-priority MMCSS tasks.",
            "Aggressive values can reduce background-task responsiveness and stability.",
            MultimediaProfile,
            "SystemResponsiveness",
            RegistryValueKind.DWord,
            10,
            100,
            new HashSet<uint>(Enumerable.Range(1, 10).Select(value => (uint)(value * 10))),
            EvidenceNote: "Documented by Microsoft as part of the multimedia class scheduler profile. The value name appears in several Windows components, including a direct code reference in avrt.dll, the user-mode MMCSS API. Being consumed by a user-mode scheduler rather than a driver is expected for this setting.")
    ];

    private static readonly IReadOnlyDictionary<string, SettingDefinition> ById =
        All.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

    public static SettingDefinition Get(string id) =>
        ById.TryGetValue(id, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Unknown setting ID: {id}");

    public static void ValidateAddress(SettingAddress address)
    {
        var expected = Get(address.SettingId).ResolveAddress(address.TargetId);
        if (expected != address)
        {
            throw new InvalidOperationException("The setting address does not match the allowlisted catalog definition.");
        }
    }
}
