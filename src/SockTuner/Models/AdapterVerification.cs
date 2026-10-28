using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SockTuner.Models;

public enum AdapterVerification { NotApplicable, Verified, Unverified }

/// <summary>
/// Whether SockTuner holds a hardware report for an adapter model. "Verified" means the model's driver properties
/// were captured on real hardware and the catalog characterised them; it is not a promise that a setting helps.
/// </summary>
public static partial class AdapterVerificationCatalog
{
    private static readonly Lazy<HashSet<string>> Known = new(Load);

    public const string DiscordInviteUrl = "https://discord.gg/HpXtkEQh43";

    public static AdapterVerification For(AdapterInfo adapter) =>
        adapter.Kind != AdapterKind.Physical ? AdapterVerification.NotApplicable : For(adapter.Description);

    public static AdapterVerification For(string description) =>
        Known.Value.Contains(Normalize(description)) ? AdapterVerification.Verified : AdapterVerification.Unverified;

    /// <summary>Case, punctuation, (R)/(TM) marks and the " #2" suffix Windows adds to a second identical adapter do not matter.</summary>
    internal static string Normalize(string description) =>
        Separators().Replace(Marks().Replace(Instance().Replace(description.Trim(), string.Empty), string.Empty).ToLowerInvariant(), " ").Trim();

    private static HashSet<string> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            Assembly.GetExecutingAssembly().GetManifestResourceNames().Single(name => name.EndsWith("verified-adapters.json", StringComparison.Ordinal)))!;
        return [.. JsonSerializer.Deserialize<string[]>(stream)!.Select(Normalize)];
    }

    [GeneratedRegex(@"\s+#\d+$")] private static partial Regex Instance();
    [GeneratedRegex(@"\((r|tm)\)", RegexOptions.IgnoreCase)] private static partial Regex Marks();
    [GeneratedRegex(@"[^a-z0-9]+")] private static partial Regex Separators();
}
