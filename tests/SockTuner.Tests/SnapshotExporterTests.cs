using System.Net.NetworkInformation;
using System.Text.Json;
using SockTuner.Models;
using SockTuner.Persistence;

namespace SockTuner.Tests;

public sealed class SnapshotExporterTests
{
    [Fact]
    public void Serialize_WritesVersionedSnapshotEnvelope()
    {
        var snapshot = new NetworkSnapshot(
            new SystemOverview("Windows", "10", "PC", 8, false, DateTimeOffset.UnixEpoch),
            [],
            [],
            null);

        using var document = JsonDocument.Parse(SnapshotExporter.Serialize(snapshot));

        Assert.Equal(12, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("PC", document.RootElement.GetProperty("snapshot").GetProperty("system").GetProperty("machineName").GetString());
    }

    [Fact]
    public void Serialize_RedactedSupportSnapshot_RemovesSensitiveValues()
    {
        var policy = new QosPolicyInfo(
            "Secret policy", "Secret owner", 7, 1, "C:\\Secret\\game.exe", "Secret user", 2, 0,
            "10.0.0.0/8", 0, 0, "203.0.113.4", 0, 0, -1, -1, 0, 0, 0,
            "https://secret.example", false, "Secret job", 0, "1");
        var adapter = new AdapterInfo(
            "SECRET-GUID", "Secret NIC", "Intel adapter", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, 1_000_000_000, "AA-BB-CC-DD-EE-FF",
            ["10.0.0.2"], ["10.0.0.1"], ["1.1.1.1"], 1, 1500, 1, 1500,
            true, true, null, null,
            [new NdisAdvancedProperty("NetworkAddress", "Network Address", "DEADBEEF0001", "", "edit", "")],
            true, null);
        var snapshot = new NetworkSnapshot(
            new SystemOverview("Windows", "10", "SECRET-PC", 8, false, DateTimeOffset.UnixEpoch),
            [adapter],
            [new RouteInfo("IPv4", "203.0.113.0/24", "192.0.2.1", 1, "Secret NIC", 10, "NetMgmt", "Indirect")],
            "Secret path C:\\Users\\name",
            QosPolicies: [policy]);

        var json = SnapshotExporter.Serialize(snapshot, redact: true);
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("redacted").GetBoolean());
        Assert.DoesNotContain("SECRET-PC", json, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DEADBEEF0001", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AA-BB-CC", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NetMgmt", json, StringComparison.Ordinal);
        Assert.Contains("CUBIC", SnapshotExporter.Serialize(snapshot with
        {
            TcpSettings = [new TcpSettingInfo("Internet", null, null, null, null, 5, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null)]
        }, redact: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_ProbeSnapshot_KeepsHardwareIdentityAndMasksPersonalValues()
    {
        var adapter = new AdapterInfo(
            "GUID-1", "Secret NIC", "Intel(R) I226-V", NetworkInterfaceType.Ethernet,
            OperationalStatus.Up, 2_500_000_000, "AA-BB-CC-DD-EE-FF",
            ["10.0.0.2"], ["10.0.0.1"], ["1.1.1.1"], 1, 1500, 1, 1500,
            true, true, null,
            new DriverInfo("Intel", "1.2.3.4", "01/01/2025", "oem5.inf", "PCI\\VEN_8086", "6.85", "PCI\\VEN_8086&DEV_125C", 0x84),
            [new NdisAdvancedProperty("*RSS", "Receive Side Scaling", "1", "1", "enum", "0: Disabled, 1: Enabled"),
             new NdisAdvancedProperty("NetworkAddress", "Network Address", "DEADBEEF0001", "", "edit", "")],
            true, null);
        var snapshot = new NetworkSnapshot(
            new SystemOverview("Windows", "10", "SECRET-PC", 8, false, DateTimeOffset.UnixEpoch),
            [adapter],
            [],
            null);

        var json = SnapshotExporter.Serialize(snapshot, probe: true);
        using var document = JsonDocument.Parse(json);
        var probeAdapter = document.RootElement.GetProperty("snapshot").GetProperty("adapters")[0];

        Assert.True(document.RootElement.GetProperty("probe").GetBoolean());
        Assert.True(document.RootElement.GetProperty("redacted").GetBoolean());
        Assert.Equal("PCI\\VEN_8086&DEV_125C", probeAdapter.GetProperty("driver").GetProperty("pnpInstanceId").GetString());
        Assert.Equal("oem5.inf", probeAdapter.GetProperty("driver").GetProperty("infPath").GetString());
        Assert.Equal("AA-BB-CC-00-00-00", probeAdapter.GetProperty("macAddress").GetString());
        Assert.Equal("GUID-1", probeAdapter.GetProperty("id").GetString());
        Assert.Equal("1", probeAdapter.GetProperty("ndisProperties")[0].GetProperty("currentValue").GetString());
        Assert.Equal("[redacted]", probeAdapter.GetProperty("ndisProperties")[1].GetProperty("currentValue").GetString());
        Assert.DoesNotContain("SECRET-PC", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DD-EE-FF", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DEADBEEF0001", json, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.2", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_ProbeSnapshot_KeepsDriverAdvertisedConstraintsAndMasksUserAssignedValues()
    {
        var capabilities = new[]
        {
            Capability("*InterruptModeration", "0", [new CapabilityChoice("0", "Disabled"), new CapabilityChoice("1", "Enabled")]),
            Capability("*JumboPacket", "1514", []) with { Minimum = 1514, Maximum = 9014, Step = 1 },
            Capability("NetworkAddress", "DEADBEEF0002", [])
        };
        var snapshot = new NetworkSnapshot(
            new SystemOverview("Windows", "10", "SECRET-PC", 8, false, DateTimeOffset.UnixEpoch),
            [], [], null, AdapterCapabilities: capabilities);

        var json = SnapshotExporter.Serialize(snapshot, probe: true);
        using var document = JsonDocument.Parse(json);
        var exported = document.RootElement.GetProperty("snapshot").GetProperty("adapterCapabilities");

        // The constraints are the whole point of a capability report and must survive redaction.
        Assert.Equal("0", exported[0].GetProperty("currentValue").GetString());
        Assert.Equal(2, exported[0].GetProperty("choices").GetArrayLength());
        Assert.Equal(1514, exported[1].GetProperty("minimum").GetInt64());
        Assert.Equal(9014, exported[1].GetProperty("maximum").GetInt64());

        // A user-assigned MAC override is personal data even inside a capability row.
        Assert.Equal("[redacted]", exported[2].GetProperty("currentValue").GetString());
        Assert.DoesNotContain("DEADBEEF0002", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_RedactedSupportSnapshot_StripsCapabilityIdentityToo()
    {
        var snapshot = new NetworkSnapshot(
            new SystemOverview("Windows", "10", "PC", 8, false, DateTimeOffset.UnixEpoch),
            [], [], null,
            AdapterCapabilities: [Capability("*InterruptModeration", "1", [])]);

        var json = SnapshotExporter.Serialize(snapshot, redact: true);

        Assert.DoesNotContain("Intel(R) Ethernet Controller I226-V", json, StringComparison.Ordinal);
        Assert.Contains("[redacted]", json, StringComparison.Ordinal);
    }

    private static AdapterSettingCapability Capability(
        string keyword, string current, IReadOnlyList<CapabilityChoice> choices) => new(
        Guid.Parse("DBE23C40-A216-4351-BC0F-CBF9519BC5CE"),
        "Ethernet 2",
        "Intel(R) Ethernet Controller I226-V",
        keyword, keyword, current, null, choices, null, null, null,
        AdapterSettingCapability.RegistrySz, false,
        TuningArea.Latency, ChangeRisk.Medium, "trade-off");
}
