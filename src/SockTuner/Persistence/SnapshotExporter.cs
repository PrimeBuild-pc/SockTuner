using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SockTuner.Models;

namespace SockTuner.Persistence;

public static class SnapshotExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(NetworkSnapshot snapshot) => JsonSerializer.Serialize(new
    {
        schemaVersion = 9,
        toolVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
        exportedAt = DateTimeOffset.Now,
        snapshot
    }, Options);
}
