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

        Assert.Equal(11, document.RootElement.GetProperty("schemaVersion").GetInt32());
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
}
