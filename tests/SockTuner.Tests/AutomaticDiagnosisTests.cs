using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services;
using SockTuner.Services.Collection;
using SockTuner.Services.Diagnosis;
using SockTuner.Services.Remediation;

namespace SockTuner.Tests;

public sealed class AutomaticDiagnosisTests
{
    private static (AdapterInfo Adapter, AdapterSettingCapability Capability) Hardware()
    {
        var assembly = typeof(NicHardwareCompatibility).Assembly;
        using var stream = assembly.GetManifestResourceStream("Hardware.Intel-Ethernet-I226-V-2.1.5.7.json")!;
        using var doc = JsonDocument.Parse(stream);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var root = doc.RootElement;
        var adapter = root.GetProperty("adapter").Deserialize<AdapterInfo>(options)!;
        var capability = root.GetProperty("capabilities").Deserialize<AdapterSettingCapability[]>(options)!
            .Single(item => item.Keyword == "*EEE") with
        { CurrentValue = "1" };
        return (adapter, capability);
    }

    [Fact]
    public void HardwareReportMustMatchTheInstalledDriverAndPropertyConstraints()
    {
        var (adapter, cap) = Hardware();
        Assert.Null(NicHardwareCompatibility.Restriction(cap, adapter.Driver));
        Assert.NotNull(NicHardwareCompatibility.Restriction(cap with { InterfaceDescription = "Unknown Ethernet" }, adapter.Driver));
        Assert.NotNull(NicHardwareCompatibility.Restriction(cap, adapter.Driver! with { Version = "99.0.0.0" }));
        Assert.NotNull(NicHardwareCompatibility.Restriction(cap, adapter.Driver! with { ComponentId = "PCI\\VEN_1234&DEV_5678" }));
        Assert.NotNull(NicHardwareCompatibility.Restriction(cap with { Step = 2 }, adapter.Driver));
        Assert.NotNull(NicHardwareCompatibility.Restriction(cap with { Choices = [new("0", "Enabled"), new("1", "Disabled")] }, adapter.Driver));
        Assert.NotNull(NicHardwareCompatibility.Restriction(cap with { Keyword = "NeverCharacterised" }, adapter.Driver));
    }

    [Fact]
    public void EvidenceProducesAnOptionalSingleChangeAndCannotCrossAdapterBoundaries()
    {
        var (adapter, cap) = Hardware();
        var report = Report() with { Interface = Environment(adapter.Id) };
        var finding = AutomaticDiagnosisSummary.EnergyTrial(report, adapter, [cap]);
        Assert.NotNull(finding);
        var action = Assert.Single(RemediationPlanner.Plan([finding!], new RemediationContext(cap.AdapterId, [cap])));
        Assert.False(action.Selected);
        Assert.Equal("nic.*EEE", Assert.Single(action.Changes).SettingId);
        Assert.Contains("not established", action.ExpectedEffect);
        Assert.Null(AutomaticDiagnosisSummary.EnergyTrial(report, adapter, [cap with { AdapterId = Guid.NewGuid() }]));
        Assert.Null(AutomaticDiagnosisSummary.EnergyTrial(report, adapter, [cap with { WriteRestriction = "Read-only" }]));
        Assert.Null(AutomaticDiagnosisSummary.EnergyTrial(report with { Gateway = Probe(1) }, adapter, [cap]));
        Assert.Null(AutomaticDiagnosisSummary.EnergyTrial(report with { Gateway = Probe(8, replies: 2) }, adapter, [cap]));
    }

    [Fact]
    public void MtuWorkaroundOnUnverifiedHardwareStaysGuidanceAndDnsDoesNotTriggerNicTuning()
    {
        var (adapter, cap) = Hardware();
        var mtu = Assert.Single(TopologyAnalyzer.Analyze(new TopologyInput(null,
            PathMtu: new PathMtuResult(PathMtuState.IcmpBlackHole, 1420, "measured"))).Findings);
        Assert.Empty(Assert.Single(RemediationPlanner.Plan([mtu], new RemediationContext(cap.AdapterId, [cap], 1420))).Changes);
        var dns = new DiagnosticFinding(DiagnosticScope.Dns, DiagnosticConfidence.High, "DNS failed", "lookup failed", "Check resolvers",
            NetworkSegment.LocalNicDriver);
        Assert.Empty(Assert.Single(RemediationPlanner.Plan([dns], new RemediationContext(cap.AdapterId, [cap], 1420, AdapterWriteAllowed: true))).Changes);
    }

    [Fact]
    public void WindowsRouteSelectionWinsOverFirstActiveAdapterAndAmbiguityIsReadOnly()
    {
        var (adapter, _) = Hardware();
        var first = adapter with { Id = Guid.NewGuid().ToString(), Name = "Unused Ethernet", Addresses = ["192.0.2.2"] };
        var routed = adapter with { Addresses = ["192.0.2.3"], Gateways = ["192.0.2.1"] };
        var snapshot = new NetworkSnapshot(new("Windows", "10", "test", 8, false, DateTimeOffset.UnixEpoch), [first, routed], [], null);
        Assert.Equal(routed.Id, RouteGatewayResolver.SelectInterface(snapshot, IPAddress.Parse("192.0.2.3")).AdapterId);
        Assert.Null(RouteGatewayResolver.SelectInterface(snapshot with { Adapters = [routed, routed] }, IPAddress.Parse("192.0.2.3")).AdapterId);
        var vpn = routed with { InterfaceType = NetworkInterfaceType.Tunnel, Driver = null };
        Assert.Contains("underlying physical link is not identified", RouteGatewayResolver.SelectInterface(snapshot with { Adapters = [vpn] }, IPAddress.Parse("192.0.2.3")).Detail);
    }

    [Fact]
    public void ComparisonRejectsSilenceChangedResolvedEndpointAndChangedEnvironment()
    {
        var baseline = Report() with { Interface = Environment("adapter-a") };
        var after = baseline with { Interface = baseline.Interface! with { ConditionsConfirmed = true } };
        Assert.Equal(ApplyOutcome.NoMeasurableChange, ApplyVerificationAnalyzer.Verify(baseline, after).Outcome);
        Assert.Equal(ApplyOutcome.NotComparable, ApplyVerificationAnalyzer.Verify(baseline, baseline).Outcome);
        Assert.Equal(ApplyOutcome.NotComparable, ApplyVerificationAnalyzer.Verify(baseline, after with { GameTarget = Probe(8, replies: 0) }).Outcome);
        Assert.False(DiagnosticComparisonService.Compare(baseline, after with { GameTarget = after.GameTarget with { Target = "other-ip" } }).Comparable);
        Assert.False(DiagnosticComparisonService.Compare(baseline with { Interface = baseline.Interface! with { AdapterId = "[redacted]" } },
            after with { Interface = after.Interface! with { AdapterId = "[redacted]" } }).Comparable);
        foreach (var changed in new[] { after.Interface! with { AdapterId = "adapter-b" }, after.Interface! with { LinkSpeed = 100_000_000 },
                     after.Interface! with { Gateway = "other-gateway" }, after.Interface! with { WifiBssid = "other-ap" } })
            Assert.Equal(ApplyOutcome.NotComparable, ApplyVerificationAnalyzer.Verify(baseline, after with { Interface = changed }).Outcome);
    }

    [Fact]
    public void ExportIncludesEvidenceAndRedactsNewInterfaceAndHealthFields()
    {
        var report = Report() with { Interface = Environment("private-adapter"), Health = [new("Adapter A", "private-evidence", "Check cable", "Interfaces", DiagnosticConfidence.Medium, ChangeRisk.Medium)] };
        var html = DiagnosticReportExporter.SerializeHtml(report);
        Assert.Contains("private-evidence", html);
        Assert.Contains("hypotheses", html);
        var redacted = DiagnosticReportExporter.SerializeJson(report, redact: true);
        Assert.DoesNotContain("private-adapter", redacted);
        Assert.DoesNotContain("private-evidence", redacted);
        Assert.DoesNotContain("192.0.2.1", redacted);
    }

    private static DiagnosticInterface Environment(string id) => new(id, "192.0.2.2", "192.0.2.1", 1_000_000_000, "Selected by Windows");
    [Fact]
    public void InterruptMappingUsesGuidAndRejectsAmbiguousOrNonNetworkDevices()
    {
        var id = Guid.NewGuid();
        var nic = new InterruptAffinityDevice("PCI\\test", "Ethernet", "Net", InterruptPolicy.MachineDefault,
            InterruptPriority.Undefined, null, true, true);
        Assert.Equal(nic, InterruptAffinityInventory.SelectNetworkDevice(id, [(id.ToString(), nic.InstanceId)], [nic]));
        Assert.Null(InterruptAffinityInventory.SelectNetworkDevice(id, [(Guid.NewGuid().ToString(), nic.InstanceId)], [nic]));
        Assert.Null(InterruptAffinityInventory.SelectNetworkDevice(id, [(id.ToString(), nic.InstanceId), (id.ToString(), "PCI\\other")], [nic]));
        Assert.Null(InterruptAffinityInventory.SelectNetworkDevice(id, [(id.ToString(), nic.InstanceId)], [nic with { IsNetwork = false }]));
    }

    private static ProbeStatistics Probe(double jitter, int replies = 30) => new("Endpoint", "192.0.2.10", 30, replies,
        100d * (30 - replies) / 30, 1, replies == 0 ? null : 5, 5, 9, 10, 12, jitter, [], null);
    private static GamingDiagnosticReport Report() => new("192.0.2.10", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(20),
        DiagnosticProfiles.All[1], DiagnosticLoadCondition.Unspecified, Probe(8), Probe(8), Probe(8),
        new("192.0.2.10", TimeSpan.Zero, ["192.0.2.10"], null), null, []);
}
