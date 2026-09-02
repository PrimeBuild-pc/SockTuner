using SockTuner.Services.Diagnosis;

namespace SockTuner.Models;

/// <summary>
/// The one-character prefix that carries a verdict's meaning without colour.
/// </summary>
/// <remarks>
/// <para>
/// Colour alone is not a channel this app can rely on. A reader with a colour vision deficiency
/// loses it entirely, and so does everyone reading the greyscale screenshot attached to a bug
/// report — which is how most of these grids are actually seen by anyone other than the user.
/// </para>
/// <para>
/// The glyphs are plain ASCII rather than symbols or emoji so they survive any font, any console
/// that a copied row is pasted into, and the HTML export. They are deliberately not a separate
/// column: a column can be resized to nothing, and this has to stay next to the word it qualifies.
/// </para>
/// </remarks>
public static class Badges
{
    /// <summary>Act on this.</summary>
    public const string Bad = "!";

    /// <summary>Look at this, but it may be fine.</summary>
    public const string Middling = "~";

    /// <summary>Nothing wrong here.</summary>
    public const string Good = "+";

    /// <summary>Context, not a problem.</summary>
    public const string Information = "i";

    /// <summary>Worth a look, without asserting it is wrong.</summary>
    public const string Question = "?";

    /// <summary>Nothing was measured, so there is nothing to judge.</summary>
    public const string Unknown = "-";

    public static string For(ChangeRisk risk) => risk switch
    {
        ChangeRisk.High => Bad,
        ChangeRisk.Medium => Middling,
        _ => Good
    };

    /// <summary>
    /// Health severities read as questions rather than as risk: "worth checking" is an invitation
    /// to look, not an assertion that something is broken.
    /// </summary>
    public static string ForSeverity(ChangeRisk severity) => severity switch
    {
        ChangeRisk.High => Bad,
        ChangeRisk.Medium => Question,
        _ => Information
    };

    public static string For(PlayabilityGrade grade) => grade switch
    {
        PlayabilityGrade.Good => Good,
        PlayabilityGrade.Playable => Middling,
        PlayabilityGrade.Poor => Bad,
        _ => Unknown
    };
}
