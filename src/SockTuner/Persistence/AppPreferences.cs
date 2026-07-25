using System.IO;
using System.Text.Json;

namespace SockTuner.Persistence;

public sealed record UserPreferences(int LogFileMegabytes = 2);

public static class AppPreferences
{
    private static readonly string PathName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PrimeBuild", "SockTuner", "Settings", "preferences.json");

    public static UserPreferences Load() => Load(PathName);

    public static void Save(UserPreferences preferences) => Save(PathName, preferences);

    internal static UserPreferences Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? Validate(JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(path)) ?? new())
                : new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    internal static void Save(string path, UserPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(Validate(preferences), new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static UserPreferences Validate(UserPreferences preferences) =>
        preferences with { LogFileMegabytes = Math.Clamp(preferences.LogFileMegabytes, 1, 64) };
}
