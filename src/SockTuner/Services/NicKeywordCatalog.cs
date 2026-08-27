using SockTuner.Models;

namespace SockTuner.Services;

public sealed record NicKeywordProfile(TuningArea Areas, ChangeRisk Risk, string TradeOff);

/// <summary>
/// Editorial metadata for NDIS advanced-property keywords: which tuning area they belong to,
/// how dangerous they are, and what the change costs. It never supplies values or ranges —
/// those come only from the driver. A keyword we have not characterised is reported as
/// high risk rather than silently treated as safe.
/// </summary>
public static class NicKeywordCatalog
{
    public static readonly NicKeywordProfile Unknown = new(
        TuningArea.Other,
        ChangeRisk.High,
        "Advertised by the driver but not yet characterised by SockTuner. Treat as experimental: "
        + "change one value at a time, measure, and roll back if behaviour changes.");

    private static readonly Dictionary<string, NicKeywordProfile> Profiles = Build();

    public static NicKeywordProfile For(string keyword) =>
        Profiles.TryGetValue(keyword, out var profile) ? profile : Unknown;

    public static bool IsCharacterised(string keyword) => Profiles.ContainsKey(keyword);

    internal static int CharacterisedCount => Profiles.Count;

    private static Dictionary<string, NicKeywordProfile> Build()
    {
        var profiles = new Dictionary<string, NicKeywordProfile>(StringComparer.OrdinalIgnoreCase);

        void Add(TuningArea areas, ChangeRisk risk, string tradeOff, params string[] keywords)
        {
            foreach (var keyword in keywords)
            {
                profiles[keyword] = new NicKeywordProfile(areas, risk, tradeOff);
            }
        }

        // ---- Interrupt and coalescing behaviour -------------------------------------------
        Add(TuningArea.Latency | TuningArea.Throughput, ChangeRisk.Medium,
            "Disabling moderation lowers best-case latency but raises interrupt rate and CPU use, "
            + "and can reduce peak throughput under load.",
            "*InterruptModeration");
        Add(TuningArea.Latency | TuningArea.Throughput, ChangeRisk.Medium,
            "A lower throttle rate reduces latency at the cost of CPU use; 'Off' can saturate a core "
            + "on a busy link.",
            "ITR");
        Add(TuningArea.Latency | TuningArea.Throughput, ChangeRisk.Medium,
            "Coalescing raises throughput and lowers CPU use, but adds receive-side latency.",
            "*RscIPv4", "*RscIPv6", "*PacketCoalescing");

        // ---- Energy saving that costs latency ---------------------------------------------
        Add(TuningArea.Latency | TuningArea.Power, ChangeRisk.Medium,
            "Energy-efficient Ethernet saves power but adds wake-up latency and destabilises the link "
            + "with some switches.",
            "*EEE", "AdvancedEEE", "EEEMaxSupportSpeed", "EnableGreenEthernet", "GigaLite");
        Add(TuningArea.Latency | TuningArea.Power, ChangeRisk.Medium,
            "Power saving reduces idle draw but can add latency on the first packet after an idle period.",
            "PowerSavingMode", "*IdleRestriction", "LowPowerEnable");

        // ---- Offloads ----------------------------------------------------------------------
        Add(TuningArea.Throughput, ChangeRisk.Medium,
            "Offload frees CPU and normally raises throughput. Disabling it is a diagnostic step, "
            + "not an optimisation.",
            "*IPChecksumOffloadIPv4", "*TCPChecksumOffloadIPv4", "*TCPChecksumOffloadIPv6",
            "*UDPChecksumOffloadIPv4", "*UDPChecksumOffloadIPv6", "*LsoV2IPv4", "*LsoV2IPv6",
            "*UsoIPv4", "*UsoIPv6");

        // ---- Buffers and flow control ------------------------------------------------------
        Add(TuningArea.Throughput | TuningArea.Latency, ChangeRisk.Medium,
            "More descriptors absorb bursts and reduce drops, but add queueing delay and pin more memory.",
            "*ReceiveBuffers", "*TransmitBuffers");
        Add(TuningArea.Throughput | TuningArea.Latency, ChangeRisk.Medium,
            "802.3x pause frames prevent drops but propagate congestion upstream and can add "
            + "head-of-line blocking delay.",
            "*FlowControl");

        // ---- Link-level settings that can sever connectivity -------------------------------
        Add(TuningArea.Throughput, ChangeRisk.High,
            "Forcing speed or duplex can drop the link or create a duplex mismatch that only shows up "
            + "as loss under load. Auto-negotiation is normally correct.",
            "*SpeedDuplex");
        Add(TuningArea.Throughput, ChangeRisk.High,
            "Every device on the path must accept the same frame size; a mismatch silently discards "
            + "large packets while small ones keep working.",
            "*JumboPacket");

        // ---- VLAN and identity -------------------------------------------------------------
        Add(TuningArea.Vlan, ChangeRisk.Medium,
            "Disabling tagging drops 802.1p priority and VLAN membership on a trunked link.",
            "*PriorityVLANTag", "*PriorityVlanTag");
        Add(TuningArea.Vlan, ChangeRisk.High,
            "A wrong VLAN ID removes the adapter from its network.",
            "RegVlanid");
        Add(TuningArea.Identity, ChangeRisk.High,
            "Overriding the hardware MAC address can break DHCP reservations, port security, and "
            + "licence activation.",
            "NetworkAddress");
        Add(TuningArea.Identity | TuningArea.Throughput, ChangeRisk.High,
            "An MTU above the path minimum causes silent black-holing of large packets.",
            "MTU");

        // ---- Wake and power-management offloads --------------------------------------------
        Add(TuningArea.Wake | TuningArea.Power, ChangeRisk.Low,
            "Affects wake-on-LAN behaviour only; no effect on active latency or throughput.",
            "*WakeOnMagicPacket", "*WakeOnPattern", "WakeOnPattern", "WakeOnMagicPacketFromS5",
            "S5WakeOnLan");
        Add(TuningArea.Wake | TuningArea.Power, ChangeRisk.Medium,
            "Reducing link speed at shutdown saves power but can prevent wake-on-LAN on some switches.",
            "WolShutdownLinkSpeed");
        Add(TuningArea.Power | TuningArea.Wake, ChangeRisk.Low,
            "Lets the NIC answer ARP/NS and rekey while the system sleeps; disabling it wakes the CPU "
            + "more often.",
            "*PMARPOffload", "*PMNSOffload", "PMARPOffload", "PMNSOffload",
            "*PMWiFiRekeyOffload", "PMWiFiRekeyOffload");
        Add(TuningArea.Power, ChangeRisk.Medium,
            "Controls whether the device sleeps while the link is down.",
            "*DeviceSleepOnDisconnect");

        // ---- Wi-Fi radio ------------------------------------------------------------------
        Add(TuningArea.WiFiRadio, ChangeRisk.Medium,
            "Restricting the band can improve stability but removes faster channels and may disconnect "
            + "if the preferred band is unavailable.",
            "BandSelection", "PreferBand", "PreferredBand", "RoamingPreferredBandType");
        Add(TuningArea.WiFiRadio | TuningArea.Throughput, ChangeRisk.High,
            "Narrowing channel width reduces interference but caps throughput; widening it can make the "
            + "link unusable in a crowded environment.",
            "BWSelection24G", "BWSelection5G", "BWSelection6G", "ChannelWidth24", "ChannelWidth52",
            "WifiBandwidth_phy0");
        Add(TuningArea.WiFiRadio, ChangeRisk.Medium,
            "More aggressive roaming switches access points sooner, which helps while moving and causes "
            + "needless disconnects while stationary.",
            "RoamAggressiveness", "RegRoamLevel", "RoamNeedIndicateTh");
        Add(TuningArea.WiFiRadio | TuningArea.Power, ChangeRisk.High,
            "Transmit power is regulatory-constrained; raising it may be unlawful and increases "
            + "interference and heat.",
            "TxPowerLevel", "IbssTxPower");
        Add(TuningArea.WiFiRadio, ChangeRisk.High,
            "Restricting the PHY mode can disconnect the adapter if the access point does not offer the "
            + "selected standard.",
            "CurrPhyMode", "WirelessMode", "IEEE11nMode", "WifiProtocol_2g", "WifiProtocol_5g",
            "WifiProtocol_6G");
        Add(TuningArea.WiFiRadio | TuningArea.Throughput, ChangeRisk.Medium,
            "Frame aggregation raises throughput and adds a little latency and retransmission cost.",
            "AMSDURx", "AMSDUTx", "ThroughputBoosterEnabled");
        Add(TuningArea.WiFiRadio | TuningArea.Power, ChangeRisk.Medium,
            "Radio power-save modes trade a little latency for battery life.",
            "MIMOPowerSaveMode", "UAPSDSupport", "uAPSDSupport");
        Add(TuningArea.WiFiRadio, ChangeRisk.Medium,
            "Affects medium access and beacon behaviour; changes interoperability with some access points.",
            "CtsToItself", "PreambleMode", "FatChannelIntolerant", "Dot11dEnable", "MCCSup",
            "AH_BcnIntv", "WfdGOOperatingChannel");
        Add(TuningArea.WiFiRadio | TuningArea.Identity, ChangeRisk.Medium,
            "MAC randomisation improves privacy but breaks MAC-based network access control.",
            "SupportMACRandom");

        return profiles;
    }
}
