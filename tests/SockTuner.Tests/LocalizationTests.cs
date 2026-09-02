using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using SockTuner.Services;

namespace SockTuner.Tests;

/// <summary>
/// The translation table is keyed by the English source text, so it rots silently: a reworded
/// button keeps working and quietly stops being translated. These read the XAML the app actually
/// ships and fail when a string in it has no entry.
/// </summary>
public class LocalizationTests
{
    private static readonly string[] Languages = ["es", "ru", "zh-Hans"];

    /// <summary>Acronyms, product names and units that read the same in every language.</summary>
    private static readonly HashSet<string> Untranslated =
    [
        "DSCP", "ECN", "Export  ▾", "IPv4", "IPv6", "MSI", "MTU", "NDIS",
        "PRE-ALPHA 0.1", "RTT ms", "SockTuner", "game.exe", "ms"
    ];

    private static readonly Regex XamlText = new(
        @"\b(?:Text|Content|Header|ToolTip|Tag|Title|AutomationProperties\.Name)=""([^""{][^""]*)""",
        RegexOptions.Compiled);

    private static string ProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SockTuner.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("SockTuner.sln not found above the test output.");
    }

    private static Dictionary<string, Dictionary<string, string>> Table()
    {
        var path = Path.Combine(ProjectRoot(), "src", "SockTuner", "Assets", "i18n", "translations.json");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path))!;
    }

    public static TheoryData<string> XamlFiles() =>
        ["MainWindow.xaml", Path.Combine("Views", "TuningPlanView.xaml")];

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void EveryXamlStringHasATranslation(string file)
    {
        var table = Table();
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot(), "src", "SockTuner", file));

        var missing = XamlText.Matches(xaml)
            .Select(match => System.Net.WebUtility.HtmlDecode(match.Groups[1].Value))
            .Where(text => Regex.IsMatch(text, "[A-Za-z]{2}"))
            .Where(text => !Untranslated.Contains(text) && !table.ContainsKey(text))
            .Distinct()
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryEntryCoversEveryLanguage()
    {
        var gaps = Table()
            .Where(entry => Languages.Any(language =>
                !entry.Value.TryGetValue(language, out var text) || text.Length == 0))
            .Select(entry => entry.Key)
            .ToArray();

        Assert.Empty(gaps);
    }

    /// <summary>
    /// A translation that loses a {0} would throw where the English did not, so the handler falls
    /// back to English — silently. This is where it stops being silent.
    /// </summary>
    [Fact]
    public void PlaceholdersSurviveTranslation()
    {
        static string[] Holes(string text) =>
            Regex.Matches(text, @"\{\d+").Select(match => match.Value).Order().ToArray();

        var drifted = Table()
            .SelectMany(entry => entry.Value.Select(translation => (entry.Key, translation)))
            .Where(pair => !Holes(pair.Key).SequenceEqual(Holes(pair.translation.Value)))
            .Select(pair => $"{pair.translation.Key}: {pair.Key}")
            .ToArray();

        Assert.Empty(drifted);
    }

    [Theory]
    [InlineData("ru", "ru")]
    [InlineData("ru-RU", "ru")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    // Traditional readers are not served simplified characters; they get the English source.
    [InlineData("zh-TW", "en")]
    [InlineData("zh-Hant", "en")]
    [InlineData("de-DE", "en")]
    [InlineData(null, null)]
    public void WindowsCultureMapsToAShippedLanguage(string? culture, string? expected)
    {
        Loc.Use(culture);
        // A null tag means "follow Windows", which is whatever this machine happens to be.
        if (expected is not null) Assert.Equal(expected, Loc.CurrentTag);
        Assert.Contains(Loc.CurrentTag, Loc.Languages.Select(language => language.Tag));
    }

    [Fact]
    public void TranslationsLoadAndFallBackToEnglish()
    {
        var strings = Loc.Load("es");

        Assert.Equal("Actualizar", strings["Refresh"]);
        Assert.False(strings.ContainsKey("IPv4"));

        Loc.Use("es");
        try
        {
            Assert.Equal("Actualizar", Loc.T("Refresh"));
            Assert.Equal("IPv4", Loc.T("IPv4"));
            Assert.Equal("El inventario falló: disco lleno", Loc.F($"Inventory failed: {"disco lleno"}"));
        }
        finally
        {
            Loc.Use("en");
        }
    }
}
