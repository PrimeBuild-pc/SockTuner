using System.Net.NetworkInformation;
using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

public sealed class InterfaceAdvisorTests
{
    private static AdapterInfo Adapter(
        string name,
        OperationalStatus status = OperationalStatus.Up,
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        string? componentId = "PCI\\VEN_8086",
        string[]? gateways = null,
        uint metric = 25,
        long speed = 1_000_000_000) =>
        new(
            Guid.NewGuid().ToString("B"),
            name,
            $"{name} description",
            type,
            status,
            speed,
            "00-00-00-00-00-00",
            ["10.0.0.2"],
            gateways ?? [],
            [],
            1, 1500, 1, 1500,
            SupportsIPv4: true,
            SupportsIPv6: true,
            InventoryError: null,
            Driver: componentId is null ? null : new DriverInfo("Vendor", "1", "—", "—", "—", "—", componentId, 0x5),
            NdisProperties: [],
            NdisSupported: true,
            NdisInventoryError: null,
            Counters: null,
            IpInterfaces: [new IpInterfaceInfo("IPv4", 1, metric, 1500, true, true, false)]);

    private static InterfaceAdvice For(IReadOnlyList<InterfaceAdvice> advice, string name) =>
        advice.Single(item => item.Name == name);

    /// <summary>An adapter identified by the INF that installed it, as the inventory records it.</summary>
    private static AdapterInfo FromInf(string name, string infPath, string componentId) =>
        Adapter(name) with
        {
            Driver = new DriverInfo("Microsoft", "1", "-", infPath, componentId, "—", "—", 0x1)
        };

    [Fact]
    public void TheAdapterCarryingTheDefaultRouteIsNeverOfferedForDisabling()
    {
        var ethernet = Adapter("Ethernet", gateways: ["10.0.0.1"], metric: 25);
        var wifi = Adapter("Wi-Fi", type: NetworkInterfaceType.Wireless80211, gateways: ["10.0.1.1"], metric: 50);

        // Both profiles, because this rule is a refusal rather than a preference.
        foreach (var singlePath in new[] { true, false })
        {
            var advice = InterfaceAdvisor.Advise([ethernet, wifi], singlePath);
            var carrying = For(advice, "Ethernet");

            Assert.Equal(InterfaceRole.Carrying, carrying.Role);
            Assert.Equal(InterfaceVerdict.Keep, carrying.Verdict);
            Assert.False(carrying.CanDisable);
        }
    }

    [Fact]
    public void TheLowestMetricDecidesWhichRouteIsCarrying()
    {
        var slowLowMetric = Adapter("Wi-Fi", type: NetworkInterfaceType.Wireless80211,
            gateways: ["10.0.1.1"], metric: 20, speed: 300_000_000);
        var fastHighMetric = Adapter("Ethernet", gateways: ["10.0.0.1"], metric: 40);

        var advice = InterfaceAdvisor.Advise([fastHighMetric, slowLowMetric], singlePathPreferred: true);

        Assert.Equal(InterfaceRole.Carrying, For(advice, "Wi-Fi").Role);
        Assert.Equal(InterfaceRole.Standby, For(advice, "Ethernet").Role);
    }

    [Fact]
    public void AVirtualAdapterThatIsUpIsFlaggedUnderEveryProfile()
    {
        var ethernet = Adapter("Ethernet", gateways: ["10.0.0.1"]);
        var vbox = Adapter("VirtualBox Host-Only", componentId: "ROOT\\VBOXNETADP");

        foreach (var singlePath in new[] { true, false })
        {
            var vboxAdvice = For(InterfaceAdvisor.Advise([ethernet, vbox], singlePath), "VirtualBox Host-Only");
            Assert.Equal(InterfaceVerdict.ConsiderDisabling, vboxAdvice.Verdict);
            Assert.True(vboxAdvice.CanDisable);
        }
    }

    [Fact]
    public void ATunnelThatIsUpIsFlagged()
    {
        var ethernet = Adapter("Ethernet", gateways: ["10.0.0.1"]);
        var teredo = Adapter("Teredo", type: NetworkInterfaceType.Tunnel, componentId: null);

        var advice = For(InterfaceAdvisor.Advise([ethernet, teredo], singlePathPreferred: false), "Teredo");

        Assert.Equal(InterfaceVerdict.ConsiderDisabling, advice.Verdict);
    }

    [Fact]
    public void ASecondPhysicalPathIsOnlyFlaggedWhenTheProfileWantsOneRoute()
    {
        var ethernet = Adapter("Ethernet", gateways: ["10.0.0.1"], metric: 25);
        var wifi = Adapter("Wi-Fi", type: NetworkInterfaceType.Wireless80211, gateways: ["10.0.1.1"], metric: 50);

        Assert.Equal(InterfaceVerdict.ConsiderDisabling,
            For(InterfaceAdvisor.Advise([ethernet, wifi], singlePathPreferred: true), "Wi-Fi").Verdict);
        Assert.Equal(InterfaceVerdict.Leave,
            For(InterfaceAdvisor.Advise([ethernet, wifi], singlePathPreferred: false), "Wi-Fi").Verdict);
    }

    [Fact]
    public void AnAdapterThatIsAlreadyDownIsLeftAloneAndCannotBeDisabledAgain()
    {
        var ethernet = Adapter("Ethernet", gateways: ["10.0.0.1"]);
        var down = Adapter("Ethernet 2", status: OperationalStatus.Down);

        var advice = For(InterfaceAdvisor.Advise([ethernet, down], singlePathPreferred: true), "Ethernet 2");

        Assert.Equal(InterfaceRole.Inactive, advice.Role);
        Assert.Equal(InterfaceVerdict.Leave, advice.Verdict);
        Assert.False(advice.CanDisable);
    }

    [Fact]
    public void LoopbackAndFilterInterfacesAreNotPresentedAsDevices()
    {
        var loopback = Adapter("Loopback", type: NetworkInterfaceType.Loopback, componentId: null);
        var filter = new AdapterInfo(
            Guid.NewGuid().ToString("B"), "QoS filter", "filter", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, 0, "—", [], [], [], 0, 0, 0, 0,
            SupportsIPv4: false, SupportsIPv6: false, InventoryError: null, Driver: null,
            NdisProperties: [], NdisSupported: false, NdisInventoryError: null);

        Assert.True(InterfaceAdvisor.IsOutOfScope(loopback));
        Assert.True(InterfaceAdvisor.IsOutOfScope(filter));

        // A machine has dozens of these per NIC; listing them would bury the real devices.
        Assert.Empty(InterfaceAdvisor.Advise([loopback, filter], singlePathPreferred: true));
    }

    [Fact]
    public void WindowsOwnRasPlumbingIsNeverSuggestedForDisabling()
    {
        // Real device IDs from a live machine: every VPN, PPPoE and dial-up connection is built on
        // these miniports, and the first version of this advisor told people to switch them off.
        var ethernet = Adapter("Ethernet", gateways: ["10.0.0.1"]);
        var wanIp = FromInf("Local Area Connection* 1", "netrasa.inf", "ms_ndiswanip");
        var wanSstp = FromInf("Local Area Connection* 4", "netsstpa.inf", "ms_sstpminiport");
        var wanIkeV2 = FromInf("Local Area Connection* 9", "netavpna.inf", "ms_agilevpnminiport");
        var kernelDebug = FromInf("Kernel debug", "kdnic.inf", @"root\kdnic");

        foreach (var plumbing in new[] { wanIp, wanSstp, wanIkeV2, kernelDebug })
        {
            Assert.True(InterfaceAdvisor.IsOutOfScope(plumbing));
            Assert.DoesNotContain(
                InterfaceAdvisor.Advise([ethernet, plumbing], singlePathPreferred: true),
                item => item.Name == plumbing.Name);
        }
    }

    [Fact]
    public void AThirdPartyVirtualAdapterUnderRootIsStillOffered()
    {
        // Real INF and component pairs from a live machine. Excluding Windows' own plumbing must
        // not quietly exclude the third-party adapters, nor the Microsoft-authored Hyper-V switch,
        // which is a legitimate thing to switch off.
        var ethernet = Adapter("Ethernet", gateways: ["10.0.0.1"]);
        (string Inf, string ComponentId)[] real =
        [
            ("oem12.inf", "sun_vboxnetadp"),
            ("oem10.inf", @"root\tap0901"),
            ("oem14.inf", "ovpn-dco"),
            ("wvms_mp_windows.inf", "vms_vsmp")
        ];

        foreach (var (inf, componentId) in real)
        {
            var virtualAdapter = FromInf("Virtual", inf, componentId);
            Assert.False(InterfaceAdvisor.IsOutOfScope(virtualAdapter));
            Assert.Equal(
                InterfaceVerdict.ConsiderDisabling,
                For(InterfaceAdvisor.Advise([ethernet, virtualAdapter], singlePathPreferred: true), "Virtual").Verdict);
        }
    }

    [Fact]
    public void EveryAdviceCarriesItsOwnEvidence()
    {
        var advice = InterfaceAdvisor.Advise(
            [Adapter("Ethernet", gateways: ["10.0.0.1"]), Adapter("Wi-Fi", type: NetworkInterfaceType.Wireless80211)],
            singlePathPreferred: true);

        Assert.All(advice, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Reason));
            Assert.False(string.IsNullOrWhiteSpace(item.Evidence));
        });
    }

    [Fact]
    public void WithNoRoutedAdapterNothingIsMistakenForTheCarryingOne()
    {
        var advice = InterfaceAdvisor.Advise([Adapter("Ethernet"), Adapter("Ethernet 2")], singlePathPreferred: true);

        Assert.DoesNotContain(advice, item => item.Role == InterfaceRole.Carrying);
    }
}
