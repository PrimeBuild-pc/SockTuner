using SockTuner.Services.Diagnosis;

namespace SockTuner.Models;

/// <summary>The one-line answers the dashboard gives; the numbers behind them live in the diagnostics section.</summary>
public static class QuickStart
{
    public static string Verdict(PlayabilityGrade? grade) => grade switch
    {
        PlayabilityGrade.Good => "Good: ready to play.",
        PlayabilityGrade.Playable => "Borderline: the preset may help.",
        PlayabilityGrade.Poor => "Not playable: see what to fix below.",
        _ => "Not checked yet."
    };
}
