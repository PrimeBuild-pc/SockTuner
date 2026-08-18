using Microsoft.Win32;
using SockTuner.Models;

namespace SockTuner.Services;

public static class SettingCatalog
{
    private const string TcpInterfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string MultimediaProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string GamesTask = MultimediaProfile + @"\Tasks\Games";

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
            new HashSet<uint>(Enumerable.Range(1, 10).Select(value => (uint)(value * 10)))),
        new(
            "tcp.interface.mtu",
            "Interface MTU",
            "IPv4 interface",
            SettingScope.AdapterInterface,
            EvidenceLevel.Documented,
            ChangeRisk.High,
            "Adapter restart",
            "Overrides the IPv4 MTU Windows uses on one interface. Removing the value restores the link-derived default.",
            "Above the smallest MTU on the path, large packets are discarded silently; below it, every packet carries more "
                + "header overhead for the same payload. Only set a value that was measured, never one that was guessed.",
            TcpInterfaces,
            "MTU",
            RegistryValueKind.DWord,
            576,
            9000),
        new(
            "tcp.interface.netbios-options",
            "NetBIOS over TCP/IP",
            "Legacy protocols",
            SettingScope.AdapterInterface,
            EvidenceLevel.Documented,
            ChangeRisk.Medium,
            "Adapter restart",
            "Sets NetBIOS over TCP/IP for one interface: 0 uses the DHCP setting, 1 enables it, 2 disables it.",
            "Disabling it removes legacy name broadcasts on that interface. File and printer sharing that still relies on "
                + "NetBIOS names — older NAS devices in particular — stops resolving. Applies to one interface only, never "
                + "to every adapter at once.",
            TcpInterfaces,
            "NetbiosOptions",
            RegistryValueKind.DWord,
            0,
            2,
            new HashSet<uint> { 0, 1, 2 }),
        new(
            "mmcss.games.gpu-priority",
            "Games task GPU priority",
            "Multimedia scheduler",
            SettingScope.System,
            EvidenceLevel.Documented,
            ChangeRisk.Medium,
            "System reboot",
            "GPU priority the multimedia scheduler gives threads registered under the Games task.",
            "Scheduling only: it moves work ahead of other work on this machine and changes nothing on the network path. "
                + "Raising it starves whatever else needs the GPU.",
            GamesTask,
            "GPU Priority",
            RegistryValueKind.DWord,
            0,
            31),
        new(
            "mmcss.games.priority",
            "Games task priority",
            "Multimedia scheduler",
            SettingScope.System,
            EvidenceLevel.Documented,
            ChangeRisk.Medium,
            "System reboot",
            "Relative thread priority the multimedia scheduler gives the Games task, from 1 to 8.",
            "Scheduling only, with no effect on latency off the machine. A game thread that never yields can starve audio "
                + "and input handling at the top of the range.",
            GamesTask,
            "Priority",
            RegistryValueKind.DWord,
            1,
            8)
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
