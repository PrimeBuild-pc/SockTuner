using System.IO;
using System.Windows;
using SockTuner.Persistence;
using SockTuner.Services;

namespace SockTuner.Views;

/// <summary>
/// The alpha risk notice, asked from one place whichever surface is about to write — the tuning
/// plan, a resolver change, an interrupt placement or the Preferences switch — so turning live
/// changes off in Preferences really does stop every one of them.
/// </summary>
internal static class WriteConsentDialog
{
    /// <summary>Raised after consent is granted or withdrawn and saved, so badges can re-read it.</summary>
    public static event EventHandler? Changed;

    /// <summary>True when consent is accepted (asking for it first if needed); otherwise why not.</summary>
    public static bool Ensure(out string? problem)
    {
        problem = null;
        var preferences = AppPreferences.Load();
        if (WriteConsent.IsAccepted(preferences)) return true;

        if (MessageBox.Show(
                Loc.T(WriteConsent.Text),
                Loc.T("Enable live network changes"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            problem = Loc.T("Live changes stay disabled until the alpha risk notice is accepted.");
            return false;
        }

        return TrySave(WriteConsent.Accept(preferences), out problem);
    }

    public static bool Revoke(out string? problem) => TrySave(WriteConsent.Revoke(AppPreferences.Load()), out problem);

    private static bool TrySave(UserPreferences preferences, out string? problem)
    {
        try
        {
            AppPreferences.Save(preferences);
            problem = null;
            Changed?.Invoke(null, EventArgs.Empty);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problem = Loc.F($"Consent could not be saved: {exception.Message}");
            return false;
        }
    }
}
