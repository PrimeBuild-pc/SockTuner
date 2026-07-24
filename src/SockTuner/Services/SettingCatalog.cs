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
            new HashSet<uint> { 0, 1 }),
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
            new HashSet<uint> { 1, 2 }),
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
            6),
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
            new HashSet<uint>(Enumerable.Range(1, 70).Select(value => (uint)value).Append(uint.MaxValue))),
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
            new HashSet<uint>(Enumerable.Range(1, 10).Select(value => (uint)(value * 10))))
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
