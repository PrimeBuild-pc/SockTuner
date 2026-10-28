using System.Windows;
using SockTuner.Services;

namespace SockTuner.Views;

/// <summary>After a compatibility report is saved: where to send it.</summary>
public partial class ReportSavedWindow : Window
{
    public ReportSavedWindow()
    {
        InitializeComponent();
        UiTranslator.Apply(this);
    }

    /// <summary>The address the user picked, or null when they closed the window.</summary>
    public string? ChosenUrl { get; private set; }

    public string GitHubUrl { get; init; } = string.Empty;

    private void GitHub_Click(object sender, RoutedEventArgs e) => Choose(GitHubUrl);

    private void Discord_Click(object sender, RoutedEventArgs e) => Choose(Models.AdapterVerificationCatalog.DiscordInviteUrl);

    private void Choose(string url)
    {
        ChosenUrl = url;
        DialogResult = true;
    }
}
