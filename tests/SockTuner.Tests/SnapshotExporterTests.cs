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

        Assert.Equal(8, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("PC", document.RootElement.GetProperty("snapshot").GetProperty("system").GetProperty("machineName").GetString());
    }
}
