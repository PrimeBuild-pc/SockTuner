using System.IO;
using System.Text.Json;

namespace SockTuner.Persistence;

public sealed record UserPreferences(
    int LogFileMegabytes = 2,
    string? AcceptedWriteConsentVersion = null,
    DateTimeOffset? WriteConsentAcceptedAt = null);

/// <summary>
/// The user-facing acknowledgement that this alpha can change live network settings. It is a
/// deliberate speed bump, not a security boundary — elevation and the driver's own constraints
/// are what actually gate a write. Versioned so a changed risk statement re-prompts.
/// </summary>
public static class WriteConsent
{
    public const string CurrentVersion = "alpha-1";

    public const string Text =
        "SockTuner is in alpha and is about to change live network settings on this computer.\n\n"
        + "• Applying a NIC property restarts that adapter, which briefly drops the link. Do not "
        + "continue over a remote session on the adapter you are changing.\n"
        + "• A wrong value can reduce throughput or stability, or leave the adapter without "
        + "connectivity until you roll back.\n"
        + "• Every change is snapshotted, verified by reading it back, and can be rolled back "
        + "exactly from the audit history.\n"
        + "• Changes marked high risk or experimental need an extra typed confirmation.\n\n"
        + "Only continue if you can recover this machine's network without remote access.";

    public static bool IsAccepted(UserPreferences preferences) =>
        string.Equals(preferences.AcceptedWriteConsentVersion, CurrentVersion, StringComparison.Ordinal);

    public static UserPreferences Accept(UserPreferences preferences) => preferences with
    {
        AcceptedWriteConsentVersion = CurrentVersion,
        WriteConsentAcceptedAt = DateTimeOffset.Now
    };
}

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
