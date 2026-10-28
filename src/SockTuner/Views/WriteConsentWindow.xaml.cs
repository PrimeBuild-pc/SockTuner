using System.Windows;
using SockTuner.Persistence;
using SockTuner.Services;

namespace SockTuner.Views;

/// <summary>
/// The alpha risk notice in the app's own style. It renders the same translated text the consent version is tied
/// to: first paragraph, the bullet lines, and the last paragraph as the warning.
/// </summary>
public partial class WriteConsentWindow : Window
{
    public WriteConsentWindow()
    {
        InitializeComponent();
        UiTranslator.Apply(this);
        var lines = Loc.T(WriteConsent.Text).Split('\n');
        var points = lines.Where(line => line.StartsWith("• ", StringComparison.Ordinal)).Select(line => line[2..]).ToArray();
        var paragraphs = lines.Where(line => line.Length > 0 && !line.StartsWith("• ", StringComparison.Ordinal)).ToArray();
        IntroText.Text = paragraphs.FirstOrDefault() ?? string.Empty;
        WarningText.Text = paragraphs.Length > 1 ? paragraphs[^1] : string.Empty;
        PointsList.ItemsSource = points;
    }

    // Declining is the safe answer, so it starts focused: Enter alone never grants consent.
    private void Window_ContentRendered(object sender, EventArgs e) => CancelButton.Focus();

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
