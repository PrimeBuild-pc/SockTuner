using System.Net.NetworkInformation;
using SockTuner.Models;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

public sealed class NetworkHealthAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AHealthyAdapterProducesNoFindings()
    {
        var report = NetworkHealthAnalyzer.Analyze(Snapshot(Adapter()), Now);

        Assert.Empty(report);
    }

    [Fact]
    public void AnOldDriverIsReportedAgainstTheDriverSection()
    {
        var adapter = Adapter(driverDate: "3-1-2019");

        var finding = Assert.Single(NetworkHealthAnalyzer.Analyze(Snapshot(adapter), Now));

        Assert.Contains("years old", finding.Title, StringComparison.Ordinal);
        Assert.Equal("NDIS & drivers", finding.Section);
    }

    [Fact]
    public void ARecentDriverIsNotReported()
    {
        var adapter = Adapter(driverDate: "6-1-2026");

        Assert.Empty(NetworkHealthAnalyzer.Analyze(Snapshot(adapter), Now));
    }

    [Fact]
    public void AGigabitClassAdapterStuckAtFastEthernetIsFlagged()
    {
        // Twenty-fold below what the part can do is a cable or port fault, not a tuning problem.
        var adapter = Adapter(description: "Intel(R) Ethernet Controller I226-V", speed: 100_000_000);

        var finding = Assert.Single(NetworkHealthAnalyzer.Analyze(Snapshot(adapter), Now));

        Assert.Contains("link negotiated", finding.Title, StringComparison.Ordinal);
        Assert.Equal(ChangeRisk.High, finding.Severity);
        Assert.Equal("Adapters", finding.Section);
    }

    [Fact]
    public void AnUnremarkableAdapterAtOneHundredMegabitIsNotFlagged()
    {
        // Without a name implying more, 100 Mbit/s may simply be the hardware.
        var adapter = Adapter(description: "Contoso Fast Ethernet", speed: 100_000_000);

        Assert.Empty(NetworkHealthAnalyzer.Analyze(Snapshot(adapter), Now));
    }

    [Fact]
    public void MixingALocalResolverWithAPublicOneIsFlagged()
    {
        var adapter = Adapter(dns: ["192.168.1.1", "8.8.8.8"]);

        var finding = Assert.Single(NetworkHealthAnalyzer.Analyze(Snapshot(adapter), Now));

        Assert.Contains("mixed", finding.Title, StringComparison.Ordinal);
        Assert.Equal("DNS resolvers", finding.Section);
        Assert.Equal(DiagnosticConfidence.High, finding.Confidence);
    }

    [Theory]
    [InlineData("192.168.1.1", "192.168.1.2")]
    [InlineData("8.8.8.8", "1.1.1.1")]
    public void ResolversOfOneKindAreNotFlagged(string first, string second)
    {
        Assert.Empty(NetworkHealthAnalyzer.Analyze(Snapshot(Adapter(dns: [first, second])), Now));
    }

    [Fact]
    public void PowerSavingLeftOnTheActiveAdapterIsReported()
    {
        var adapter = Adapter(properties: [
            new NdisAdvancedProperty("*EEE", "Energy Efficient Ethernet", "1", "1", "enum", "0,1")
        ]);

        var finding = Assert.Single(NetworkHealthAnalyzer.Analyze(Snapshot(adapter), Now));

        Assert.Contains("power saving", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerSavingAlreadyOffIsNotReported()
    {
        var adapter = Adapter(properties: [
            new NdisAdvancedProperty("*EEE", "Energy Efficient Ethernet", "0", "1", "enum", "0,1")
        ]);

        Assert.Empty(NetworkHealthAnalyzer.Analyze(Snapshot(adapter), Now));
    }

    [Fact]
    public void ADownAdapterIsNotInspected()
    {
        // Nothing about an adapter carrying no traffic is worth reporting as a problem.
        var adapter = Adapter(driverDate: "3-1-2015", status: OperationalStatus.Down);

        Assert.Empty(NetworkHealthAnalyzer.Analyze(Snapshot(adapter), Now));
    }

    [Fact]
    public void FindingsAreOrderedWithTheMostSevereFirst()
    {
        var adapter = Adapter(
            description: "Intel(R) Ethernet Controller I226-V",
            speed: 100_000_000,
            driverDate: "3-1-2019");

        var findings = NetworkHealthAnalyzer.Analyze(Snapshot(adapter), Now);

        Assert.Equal(2, findings.Count);
        Assert.Equal(ChangeRisk.High, findings[0].Severity);
    }

    [Fact]
    public void EveryFindingNamesASectionAndSaysWhatToDo()
    {
        var adapter = Adapter(driverDate: "3-1-2019", dns: ["192.168.1.1", "8.8.8.8"]);

        Assert.All(NetworkHealthAnalyzer.Analyze(Snapshot(adapter), Now), finding =>
        {
            Assert.False(string.IsNullOrWhiteSpace(finding.Section));
            Assert.False(string.IsNullOrWhiteSpace(finding.Action));
            Assert.False(string.IsNullOrWhiteSpace(finding.Evidence));
        });
    }

    [Theory]
    [InlineData("3-1-2019", true)]
    [InlineData("03-01-2019", true)]
    [InlineData("2019-03-01", true)]
    [InlineData("", false)]
    [InlineData("not a date", false)]
    public void DriverDatesAreParsedInTheFormsWindowsReports(string value, bool expected) =>
        Assert.Equal(expected, NetworkHealthAnalyzer.TryParseDriverDate(value, out _));

    private static AdapterInfo Adapter(
        string description = "Contoso 2.5GbE",
        long speed = 2_500_000_000,
        string driverDate = "6-1-2026",
        OperationalStatus status = OperationalStatus.Up,
        string[]? dns = null,
        NdisAdvancedProperty[]? properties = null) =>
        new(
            "{11111111-2222-3333-4444-555555555555}",
            "Ethernet",
            description,
            NetworkInterfaceType.Ethernet,
            status,
            speed,
            "AA-BB-CC-DD-EE-FF",
            [],
            [],
            dns ?? [],
            0, 0, 0, 0, false, false, null,
            new DriverInfo("Contoso", "1.0.0.0", driverDate, "oem1.inf", "pci\\contoso", "6.85", "PCI\\X", 4),
            properties ?? [],
            true,
            null,
            null,
            null);

    private static NetworkSnapshot Snapshot(params AdapterInfo[] adapters) =>
        new(new SystemOverview("Windows 11", "10.0.26200", "PC", 16, false, DateTimeOffset.Now), adapters, [], null);
}
