using System.Globalization;
using System.Net.NetworkInformation;
using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

/// <summary>
/// One thing worth a user's attention, and the tab that does something about it.
/// </summary>
/// <param name="Section">
/// Where the fix lives. This is what makes the check a starting point rather than a wall of text:
/// every finding routes to the surface that can act on it.
/// </param>
public sealed record HealthFinding(
    string Title,
    string Evidence,
    string Action,
    string Section,
    DiagnosticConfidence Confidence,
    ChangeRisk Severity)
{
    public string SeverityDisplay => Severity switch
    {
        ChangeRisk.High => "Worth fixing",
        ChangeRisk.Medium => "Worth checking",
        _ => "For information"
    };
}

/// <summary>
/// Diagnosis layer: reads the inventory that has already been captured and reports the problems it
/// can see without measuring anything.
/// </summary>
/// <remarks>
/// <para>
/// This runs on every inventory refresh, so it must be cheap and pure: no probes, no writes, no
/// network traffic. Everything it reports comes from state Windows already told us about.
/// </para>
/// <para>
/// The checks are the ones that account for most real reports and that a latency test alone cannot
/// see: a driver years out of date, filter drivers stacked on the datapath, a link negotiated far
/// below what the adapter can do, power saving on the adapter carrying the traffic, resolvers that
/// disagree with each other, and offloads someone disabled years ago and forgot.
/// </para>
/// </remarks>
public static class NetworkHealthAnalyzer
{
    /// <summary>A driver older than this is worth looking at; NIC vendors ship real fixes.</summary>
    private const int StaleDriverYears = 3;

    /// <summary>Filter components that sit in the datapath and are worth naming when latency is the complaint.</summary>
    private static readonly string[] NotableFilters =
        ["npcap", "npf", "virtualbox", "vmware", "wireshark", "pcap", "vpn", "proxifier", "netlimiter", "killer"];

    public static IReadOnlyList<HealthFinding> Analyze(NetworkSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var findings = new List<HealthFinding>();
        var active = snapshot.Adapters
            .Where(adapter => adapter.Kind == AdapterKind.Physical && adapter.Status == OperationalStatus.Up)
            .ToArray();

        foreach (var adapter in active)
        {
            StaleDriver(adapter, now, findings);
            UnderNegotiatedLink(adapter, findings);
            PowerSavingOnActiveAdapter(adapter, findings);
        }

        AntiCheatRunning(findings);
        MixedResolvers(active, findings);
        CompetingDefaultRoutes(active, findings);
        DatapathFilters(snapshot, active, findings);
        DisabledOffloads(snapshot, active, findings);

        return findings
            .OrderByDescending(finding => finding.Severity)
            .ThenByDescending(finding => finding.Confidence)
            .ToArray();
    }

    private static void StaleDriver(AdapterInfo adapter, DateTimeOffset now, List<HealthFinding> findings)
    {
        if (adapter.Driver is not { } driver || !TryParseDriverDate(driver.Date, out var date)) return;
        var years = (now - date).TotalDays / 365.25;
        if (years < StaleDriverYears) return;

        findings.Add(new HealthFinding(
            $"{adapter.Name}: network driver is {years:0.#} years old",
            $"{driver.Provider} {driver.Version}, dated {date:yyyy-MM-dd}.",
            "Check the adapter vendor for a newer driver. Vendors ship real fixes for interrupt handling and link "
            + "stability, and Windows Update often keeps an older one. Tune the driver's properties only after that.",
            "NDIS & drivers",
            DiagnosticConfidence.Medium,
            ChangeRisk.Medium));
    }

    // A 2.5G adapter sitting at 100 Mbps is nearly always a cable or port fault, and no amount of
    // tuning recovers the missing twenty-fold.
    private static void UnderNegotiatedLink(AdapterInfo adapter, List<HealthFinding> findings)
    {
        if (adapter.InterfaceType != NetworkInterfaceType.Ethernet || adapter.SpeedBitsPerSecond <= 0) return;
        if (adapter.SpeedBitsPerSecond > 100_000_000) return;

        var suggestsFaster = adapter.Description.Contains("2.5G", StringComparison.OrdinalIgnoreCase)
            || adapter.Description.Contains("Gigabit", StringComparison.OrdinalIgnoreCase)
            || adapter.Description.Contains("I226", StringComparison.OrdinalIgnoreCase)
            || adapter.Description.Contains("I225", StringComparison.OrdinalIgnoreCase);
        if (!suggestsFaster) return;

        findings.Add(new HealthFinding(
            $"{adapter.Name}: link negotiated at {adapter.SpeedDisplay}",
            $"{adapter.Description} reports {adapter.SpeedDisplay}, below what the adapter name implies.",
            "Usually a cable or switch port rather than a setting: try a different cable and port before changing "
            + "speed and duplex by hand, which hides the fault rather than fixing it.",
            "Adapters",
            DiagnosticConfidence.Medium,
            ChangeRisk.High));
    }

    private static void PowerSavingOnActiveAdapter(AdapterInfo adapter, List<HealthFinding> findings)
    {
        var enabled = adapter.NdisProperties
            .Where(property => IsEnabled(property)
                && (property.Keyword.Equals("*EEE", StringComparison.OrdinalIgnoreCase)
                    || property.Keyword.Contains("GreenEthernet", StringComparison.OrdinalIgnoreCase)
                    || property.Keyword.Contains("PowerSaving", StringComparison.OrdinalIgnoreCase)
                    || property.Keyword.Equals("*IdleRestriction", StringComparison.OrdinalIgnoreCase)))
            .Select(property => property.DisplayName)
            .ToArray();
        if (enabled.Length == 0) return;

        findings.Add(new HealthFinding(
            $"{adapter.Name}: link power saving is on",
            $"Enabled: {string.Join(", ", enabled)}.",
            "These let the link idle down and add wake-up delay on the first packet after a quiet period, which is "
            + "felt as an occasional spike rather than as steady latency. Each is a driver-advertised property.",
            "NDIS & drivers",
            DiagnosticConfidence.Medium,
            ChangeRisk.Medium));
    }

    /// <summary>
    /// Kernel-level anti-cheat drivers watch for exactly the kind of driver and device changes this
    /// app makes. Nothing here is against the rules, but a change applied mid-session is worth
    /// knowing about before it turns into an unexplained kick.
    /// </summary>
    private static void AntiCheatRunning(List<HealthFinding> findings)
    {
        // Queried through WMI rather than ServiceController: System.Management is already a
        // dependency, and this avoids taking a package for one lookup.
        string[] services = ["EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "vgk", "vgc", "ACE-BASE", "ACE-GAME"];
        var running = new List<string>();
        try
        {
            var names = string.Join(" OR ", services.Select(name => $"Name='{name}'"));
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT Name, State FROM Win32_Service WHERE ({names})");
            foreach (var item in searcher.Get().Cast<System.Management.ManagementBaseObject>())
            {
                using (item)
                {
                    if (string.Equals(item["State"] as string, "Running", StringComparison.OrdinalIgnoreCase))
                    {
                        running.Add(item["Name"] as string ?? "unknown");
                    }
                }
            }
        }
        catch (Exception exception) when (exception is System.Management.ManagementException
            or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return;
        }

        if (running.Count == 0) return;

        findings.Add(new HealthFinding(
            $"{running.Count} anti-cheat service(s) are running",
            string.Join(", ", running),
            "Apply adapter and driver changes with the game closed, then start it. A driver reload while a "
            + "kernel-level anti-cheat is watching can end the session, and the cause is hard to attribute afterwards.",
            "Tuning plan",
            DiagnosticConfidence.High,
            ChangeRisk.Medium));
    }

    // Mixing the router's resolver with a public one means answers come from whichever replies
    // first, so local names resolve intermittently — a classic "it works sometimes" report.
    private static void MixedResolvers(IReadOnlyList<AdapterInfo> active, List<HealthFinding> findings)
    {
        foreach (var adapter in active)
        {
            var servers = adapter.DnsServers
                .Where(address => System.Net.IPAddress.TryParse(address, out var parsed)
                    && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .ToArray();
            if (servers.Length < 2) continue;

            var privateCount = servers.Count(IsPrivate);
            if (privateCount == 0 || privateCount == servers.Length) continue;

            findings.Add(new HealthFinding(
                $"{adapter.Name}: local and public resolvers are mixed",
                $"Configured: {string.Join(", ", servers)}.",
                "Windows does not use these strictly in order, so a name that only the local resolver knows will "
                + "resolve some of the time and fail the rest. Use one or the other, then benchmark it.",
                "DNS resolvers",
                DiagnosticConfidence.High,
                ChangeRisk.Medium));
        }
    }

    private static void CompetingDefaultRoutes(IReadOnlyList<AdapterInfo> active, List<HealthFinding> findings)
    {
        var withGateway = active.Where(adapter => adapter.Gateways.Count > 0).ToArray();
        if (withGateway.Length < 2) return;

        var wireless = withGateway.Where(adapter => adapter.InterfaceType == NetworkInterfaceType.Wireless80211).ToArray();
        if (wireless.Length == 0 || wireless.Length == withGateway.Length) return;

        findings.Add(new HealthFinding(
            "Wired and wireless are both online with a gateway",
            $"{string.Join(", ", withGateway.Select(adapter => $"{adapter.Name} ({adapter.MetricDisplay})"))}.",
            "Windows picks one by interface metric, and it is not always the one expected. If traffic should take the "
            + "wired link, confirm its metric is the lower of the two rather than disabling the other adapter.",
            "Routes & DNS",
            DiagnosticConfidence.High,
            ChangeRisk.Medium));
    }

    private static void DatapathFilters(
        NetworkSnapshot snapshot, IReadOnlyList<AdapterInfo> active, List<HealthFinding> findings)
    {
        var activeIds = active.Select(adapter => adapter.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filters = (snapshot.NetworkBindings ?? [])
            .Where(binding => binding.Enabled
                && activeIds.Contains(binding.AdapterId.ToString("B").ToUpperInvariant())
                && NotableFilters.Any(name => binding.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase)
                    || binding.ComponentId.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .Select(binding => binding.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (filters.Length == 0) return;

        findings.Add(new HealthFinding(
            $"{filters.Length} capture or virtualisation filter(s) bound to the active adapter",
            string.Join(", ", filters),
            "Each filter sits in the datapath of every packet. They are usually harmless, but they are the first thing "
            + "to remove when chasing unexplained latency, and one left behind by an uninstalled tool is common.",
            "Network bindings",
            DiagnosticConfidence.Medium,
            ChangeRisk.Low));
    }

    // Offloads disabled by an old guide keep costing CPU and throughput long after the reason is
    // forgotten. Reported, never changed automatically: disabling one is also a valid diagnostic step.
    private static void DisabledOffloads(
        NetworkSnapshot snapshot, IReadOnlyList<AdapterInfo> active, List<HealthFinding> findings)
    {
        var activeIds = active.Select(adapter => adapter.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var disabled = (snapshot.AdapterOffloads ?? [])
            .Where(offload => activeIds.Contains(offload.AdapterId.ToString("B").ToUpperInvariant())
                && offload.State.Contains("Disabled", StringComparison.OrdinalIgnoreCase))
            .Select(offload => offload.Feature)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (disabled.Length == 0) return;

        findings.Add(new HealthFinding(
            $"{disabled.Length} hardware offload(s) disabled on the active adapter",
            string.Join(", ", disabled),
            "Offloads normally raise throughput and lower CPU use. Disabling them is a diagnostic step, not a tuning: "
            + "if this was not deliberate and recent, turning them back on is usually the better default.",
            "Offloads",
            DiagnosticConfidence.Medium,
            ChangeRisk.Medium));
    }

    private static bool IsEnabled(NdisAdvancedProperty property) =>
        !string.Equals(property.CurrentValue, "0", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(property.CurrentValue)
        && !string.Equals(property.CurrentValue, "—", StringComparison.Ordinal);

    private static bool IsPrivate(string address)
    {
        if (!System.Net.IPAddress.TryParse(address, out var parsed)) return false;
        var octets = parsed.GetAddressBytes();
        return octets[0] switch
        {
            10 => true,
            127 => true,
            172 => octets[1] is >= 16 and <= 31,
            192 => octets[1] == 168,
            169 => octets[1] == 254,
            _ => false
        };
    }

    internal static bool TryParseDriverDate(string? value, out DateTimeOffset date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        // Windows reports driver dates as M-d-yyyy in the class key; the invariant forms are tried
        // as well so a differently formatted provider string is not silently treated as missing.
        string[] formats = ["M-d-yyyy", "MM-dd-yyyy", "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy"];
        if (DateTimeOffset.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out date))
        {
            return true;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out date);
    }
}
