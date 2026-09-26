using System.Net.NetworkInformation;
using System.Text.Json;
using SockTuner.Models;
using SockTuner.Persistence;

namespace SockTuner.Tests;

public sealed class CompatibilityReportExporterTests
{
    [Fact]
    public void Serialize_KeepsCapabilityConstraintsWithoutPersistentMachineIdentifiers()
    {
        var realId = Guid.Parse("DBE23C40-A216-4351-BC0F-CBF9519BC5CE");
        var adapter = new AdapterInfo(
            realId.ToString(), "Secret NIC", "Intel(R) Ethernet Controller I226-V",
            NetworkInterfaceType.Ethernet, OperationalStatus.Up, 2_500_000_000,
            "AA-BB-CC-DD-EE-FF", ["10.0.0.2"], ["10.0.0.1"], ["1.1.1.1"],
            7, 1500, 7, 1500, true, true, null,
            new DriverInfo("Intel", "2.1.5.7", "2025-01-01", "oem42.inf",
                "PCI\\VEN_8086&DEV_125C&SUBSYS_DEADBEEF&REV_06", "6.85",
                "PCI\\VEN_8086&DEV_125C\\SECRET-INSTANCE", 0x84),
            [new("*RSS", "Receive Side Scaling", "1", "1", "enum", "0: Disabled, 1: Enabled"),
             new("NetworkAddress", "Network Address", "DEADBEEF0001", "", "edit", "")],
            true, null);
        var capability = new AdapterSettingCapability(
            realId, "Secret NIC", adapter.Description, "*RSS", "Receive Side Scaling", "1", "1",
            [new("0", "Disabled"), new("1", "Enabled")], null, null, null,
            AdapterSettingCapability.RegistrySz, false, TuningArea.Throughput, ChangeRisk.Medium, "trade-off");
        var snapshot = new NetworkSnapshot(
            new("Windows", "10.0.26100", "SECRET-PC", 16, false,
                DateTimeOffset.Parse("2026-09-26T20:00:00+02:00")),
            [adapter], [], null, AdapterCapabilities: [capability]);

        var json = CompatibilityReportExporter.Serialize(snapshot);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var exportedAdapter = root.GetProperty("adapters")[0];

        Assert.Equal("socktuner.compatibility", root.GetProperty("reportType").GetString());
        Assert.True(root.GetProperty("redacted").GetBoolean());
        Assert.Equal("00000000-0000-0000-0000-000000000001", exportedAdapter.GetProperty("id").GetString());
        Assert.Equal("PCI\\VEN_8086&DEV_125C&REV_06",
            exportedAdapter.GetProperty("driver").GetProperty("componentId").GetString());
        Assert.Equal(2, exportedAdapter.GetProperty("capabilities")[0].GetProperty("choices").GetArrayLength());
        Assert.All(exportedAdapter.GetProperty("ndisProperties").EnumerateArray(), property =>
            Assert.Equal("[redacted]", property.GetProperty("currentValue").GetString()));
        Assert.All(exportedAdapter.GetProperty("capabilities").EnumerateArray(), capabilityRow =>
            Assert.Equal("[redacted]", capabilityRow.GetProperty("currentValue").GetString()));
        Assert.False(root.GetProperty("system").TryGetProperty("logicalProcessors", out _));

        foreach (var secret in new[]
        {
            realId.ToString(), "SECRET-PC", "Secret NIC", "AA-BB-CC", "10.0.0.2", "1.1.1.1",
            "oem42.inf", "SUBSYS", "SECRET-INSTANCE", "DEADBEEF0001", "2026-09-26T20:00:00"
        })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("PCI\\VEN_10EC&DEV_8125&SUBSYS_7C751462&REV_04", "PCI\\VEN_10EC&DEV_8125&REV_04")]
    [InlineData("USB\\VID_1234&PID_5678&MI_00", "USB\\VID_1234&PID_5678")]
    [InlineData("ROOT\\VEN_8086&DEV_125C", "Unavailable")]
    [InlineData("PCI\\VEN_8086&DEV_12ZZ", "Unavailable")]
    [InlineData("PCI\\VEN_8086EVIL&DEV_125C", "Unavailable")]
    [InlineData("", "Unavailable")]
    public void SanitizeComponentId_KeepsOnlyHardwareClassFields(string input, string expected) =>
        Assert.Equal(expected, CompatibilityReportExporter.SanitizeComponentId(input));
}
