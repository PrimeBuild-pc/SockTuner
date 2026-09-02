using System.Collections.Frozen;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SockTuner.Services;

/// <summary>
/// English is the source language and also the key: every string in this app is written in English
/// in XAML or in code, and translating one is a dictionary lookup on that English text. There is no
/// key catalogue to keep in sync with the UI, a translator reads sentences rather than
/// <c>MainWindow_Btn_Refresh</c>, and anything not yet translated degrades to readable English
/// instead of to a resource id.
/// </summary>
public static class Loc
{
    /// <summary>Language name shown in its own language: a Russian user looks for "Русский".</summary>
    public sealed record Language(string Tag, string NativeName)
    {
        // App.xaml's ComboBox template renders the selection box from ItemTemplate, which
        // DisplayMemberPath never sets, so the name has to come from ToString().
        public override string ToString() => NativeName;
    }

    public static readonly IReadOnlyList<Language> Languages =
    [
        new("en", "English"),
        new("es", "Español"),
        new("ru", "Русский"),
        new("zh-Hans", "简体中文")
    ];

    private static FrozenDictionary<string, string> _strings = FrozenDictionary<string, string>.Empty;

    public static string CurrentTag { get; private set; } = "en";

    /// <summary>
    /// Selects a language. A null or unknown tag means "decide from Windows", so a Russian install
    /// starts in Russian without anyone opening Preferences; an explicit choice always wins.
    /// </summary>
    public static void Use(string? tag)
    {
        CurrentTag = Resolve(tag) ?? Resolve(CultureInfo.CurrentUICulture.Name) ?? "en";
        _strings = CurrentTag == "en"
            ? FrozenDictionary<string, string>.Empty
            : Load(CurrentTag);
    }

    /// <summary>"ru-RU" and "zh-Hant-TW" both have to land on something, or on nothing.</summary>
    private static string? Resolve(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var exact = Languages.FirstOrDefault(language =>
            string.Equals(language.Tag, tag, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Tag;

        // zh-CN / zh-SG carry simplified script; zh-TW / zh-HK do not, and get English rather than
        // characters a traditional reader would find wrong.
        if (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return tag.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                || tag.Contains("TW", StringComparison.OrdinalIgnoreCase)
                || tag.Contains("HK", StringComparison.OrdinalIgnoreCase)
                || tag.Contains("MO", StringComparison.OrdinalIgnoreCase)
                ? null
                : "zh-Hans";

        var primary = tag.Split('-')[0];
        return Languages.FirstOrDefault(language =>
            string.Equals(language.Tag, primary, StringComparison.OrdinalIgnoreCase))?.Tag;
    }

    /// <summary>The English text, or its translation when there is one.</summary>
    public static string T(string english) =>
        _strings.TryGetValue(english, out var translated) && translated.Length > 0 ? translated : english;

    /// <summary>
    /// Translates an interpolated string. The holes are pulled out first, so
    /// <c>Loc.F($"Inventory failed: {message}")</c> looks up "Inventory failed: {0}" and the call
    /// site stays a normal interpolated string.
    /// </summary>
    public static string F(TranslatedString text) => text.Resolve();

    /// <summary>All translations for one language, embedded in the exe. Read once per switch.</summary>
    internal static FrozenDictionary<string, string> Load(string tag)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("SockTuner.Assets.i18n.translations.json");
        if (stream is null) return FrozenDictionary<string, string>.Empty;
        return Load(stream, tag);
    }

    internal static FrozenDictionary<string, string> Load(Stream stream, string tag)
    {
        var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)
            ?? [];
        return all
            .Where(entry => entry.Value.TryGetValue(tag, out var value) && value.Length > 0)
            .ToFrozenDictionary(entry => entry.Key, entry => entry.Value[tag], StringComparer.Ordinal);
    }
}

/// <summary>
/// Rebuilds an interpolated string as a composite format string ("Diagnosing {0}…") plus its
/// arguments, so the sentence can be looked up before it is filled in. Without this every
/// interpolated call site would have to be rewritten by hand into a format string and a positional
/// argument list, which is where argument-order bugs come from.
/// </summary>
[InterpolatedStringHandler]
public ref struct TranslatedString
{
    private readonly StringBuilder _format;
    private readonly List<object?> _arguments;

    public TranslatedString(int literalLength, int formattedCount)
    {
        _format = new StringBuilder(literalLength + (formattedCount * 4));
        _arguments = new List<object?>(formattedCount);
    }

    public void AppendLiteral(string value) =>
        _format.Append(value.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));

    public void AppendFormatted<T>(T value) => Hole(value, null);

    public void AppendFormatted<T>(T value, string? format) => Hole(value, format);

    private void Hole<T>(T value, string? format)
    {
        _format.Append('{').Append(_arguments.Count);
        if (!string.IsNullOrEmpty(format)) _format.Append(':').Append(format);
        _format.Append('}');
        _arguments.Add(value);
    }

    internal string Resolve()
    {
        var english = _format.ToString();
        var arguments = _arguments.ToArray();
        try
        {
            return string.Format(CultureInfo.CurrentCulture, Loc.T(english), arguments);
        }
        catch (FormatException)
        {
            // A translation with a mangled placeholder must not take the app down; the test suite
            // fails on placeholder drift, and at runtime the English sentence is still true.
            return string.Format(CultureInfo.CurrentCulture, english, arguments);
        }
    }
}

/// <summary>
/// Translates a window in place after it is built. The alternative — a markup extension on all 450
/// XAML strings — is the same translation table reached through 450 edits, and it would still leave
/// the strings set from code to handle separately.
/// </summary>
/// <remarks>
/// Running twice is harmless: a translated string is not itself an English key, so the second pass
/// finds nothing to change.
/// </remarks>
public static class UiTranslator
{
    public static void Apply(DependencyObject root)
    {
        if (Loc.CurrentTag == "en") return;
        Walk(root);
    }

    private static void Walk(DependencyObject node)
    {
        switch (node)
        {
            // The header is also an identifier: OpenHealthSection_Click matches a HealthFinding's
            // section against it, and the analyzers name those sections in English. The English
            // original moves to Tag so that routing keeps working in every language.
            case TabItem tab:
                if (tab.Header is string tabHeader)
                {
                    tab.Tag ??= tabHeader;
                    tab.Header = Loc.T(tabHeader);
                }
                break;
            case HeaderedContentControl headered when headered.Header is string header:
                headered.Header = Loc.T(header);
                break;
            case HeaderedItemsControl items when items.Header is string itemsHeader:
                items.Header = Loc.T(itemsHeader);
                break;
            case TextBlock text when !HasBinding(text, TextBlock.TextProperty):
                text.Text = Loc.T(text.Text);
                break;
            case ContentControl content
                when content.Content is string label && !HasBinding(content, ContentControl.ContentProperty):
                content.Content = Loc.T(label);
                break;
        }

        if (node is FrameworkElement element)
        {
            if (element.ToolTip is string tip) element.ToolTip = Loc.T(tip);

            // The placeholder of a PlaceholderBox is its Tag; on anything else Tag is not text.
            if (element is TextBoxBase && element.Tag is string placeholder) element.Tag = Loc.T(placeholder);

            if (AutomationProperties.GetName(element) is { Length: > 0 } automationName)
                AutomationProperties.SetName(element, Loc.T(automationName));

            // A context menu is not a logical child of the control that owns it.
            if (element.ContextMenu is { } menu) Walk(menu);

            // Nor are DataGrid columns: they are not in any tree, they are a collection.
            if (element is DataGrid grid)
                foreach (var column in grid.Columns)
                    if (column.Header is string columnHeader)
                        column.Header = Loc.T(columnHeader);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject dependency)
                Walk(dependency);
    }

    /// <summary>A bound TextBlock shows data, not UI text, and overwriting it would clear the binding.</summary>
    private static bool HasBinding(DependencyObject node, DependencyProperty property) =>
        System.Windows.Data.BindingOperations.GetBindingBase(node, property) is not null;
}
