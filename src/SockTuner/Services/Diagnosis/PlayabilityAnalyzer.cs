using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

public enum PlayabilityGrade
{
    /// <summary>Inside the budget the tick rate allows.</summary>
    Good,

    /// <summary>Over the comfortable limit but still inside one server update.</summary>
    Playable,

    /// <summary>Past the point where an input can be placed in a predictable update.</summary>
    Poor,

    /// <summary>Nothing came back, so there is nothing to grade.</summary>
    Unmeasured
}

/// <summary>One measured number against the budget its game's tick rate allows it.</summary>
public sealed record MetricVerdict(
    string Name,
    PlayabilityGrade Grade,
    string Measured,
    string Budget,
    string Explanation)
{
    public string GradeDisplay => PlayabilityVerdict.Display(Grade);
}

/// <summary>
/// What a measured path means for one game. The grade is the worst of the three metrics rather than
/// their average, and the metric that decided it is named — 20 ms of ping with 2 % loss is not a
/// good connection, and averaging that away is exactly the flattery this app exists to remove.
/// </summary>
public sealed record PlayabilityVerdict(
    GameProfile Game,
    PlayabilityGrade Grade,
    string DecidedBy,
    string Headline,
    string Detail,
    IReadOnlyList<MetricVerdict> Metrics)
{
    public string GradeDisplay => Display(Grade);

    public static string Display(PlayabilityGrade grade) => grade switch
    {
        PlayabilityGrade.Good => "Good",
        PlayabilityGrade.Playable => "Playable, borderline",
        PlayabilityGrade.Poor => "Not playable",
        _ => "Not measured"
    };
}

/// <summary>
/// Diagnosis layer: turns a probe result into a statement about one game, using thresholds derived
/// from that game's tick rate rather than fixed numbers.
/// </summary>
/// <remarks>
/// <para>
/// The jitter limits are arithmetic: a server that updates every 7.8 ms cannot place an input that
/// arrives 10 ms out of position, so half a tick is the comfortable limit and one tick is the edge.
/// Both are capped, because on a 20 Hz game the tick maths alone would call 50 ms of jitter fine.
/// </para>
/// <para>
/// The latency limits are a judgement and are labelled as one. The loss limits are neither: a
/// dropped packet is a dropped input at every tick rate, so anything above zero costs inputs and
/// anything above one per cent is not good enough for any of these games.
/// </para>
/// <para>
/// This measures nothing itself. It reads a run that has already happened.
/// </para>
/// </remarks>
public static class PlayabilityAnalyzer
{
    /// <summary>Above zero, inputs are being lost; above this, the connection is not good enough for any of them.</summary>
    private const double PlayableLossPercent = 1;

    /// <summary>
    /// Windows reports an ICMP round trip in whole milliseconds. Below this budget the jitter
    /// figure is quantised at a scale that matters, and the verdict says so rather than implying a
    /// precision the measurement does not have.
    /// </summary>
    private const double QuantisationFloorMs = 4;

    public static PlayabilityVerdict Judge(ProbeStatistics statistics, GameProfile game)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(game);

        if (statistics.Received == 0)
        {
            return new PlayabilityVerdict(
                game,
                PlayabilityGrade.Unmeasured,
                "no replies",
                $"Nothing came back from {statistics.Target}",
                "Every probe went unanswered. That is either a path that is down or a host that does not answer ICMP; "
                    + "the probe cannot tell those apart. Try the gateway and a neutral reference first — if those answer "
                    + "and this one does not, the endpoint is refusing rather than the line failing.",
                []);
        }

        var metrics = new List<MetricVerdict>
        {
            JudgeLatency(statistics, game),
            JudgeJitter(statistics, game),
            JudgeLoss(statistics)
        };

        var worst = metrics.MaxBy(metric => Severity(metric.Grade))!;
        return new PlayabilityVerdict(
            game,
            worst.Grade,
            worst.Name,
            worst.Grade switch
            {
                PlayabilityGrade.Good => $"Good for {game.DisplayName}",
                PlayabilityGrade.Playable => $"Playable, borderline for {game.DisplayName}",
                _ => $"Not playable for {game.DisplayName}"
            },
            worst.Explanation,
            metrics);
    }

    /// <summary>
    /// Adds what a long run can say and a spot test cannot: how much of the measured time the line
    /// was actually answering. A connection that replies to 99.9 % of packets but goes silent for
    /// four seconds twice an evening is the exact fault a game notices and an average hides.
    /// </summary>
    public static string Availability(StabilityReport stability)
    {
        ArgumentNullException.ThrowIfNull(stability);
        if (stability.AvailabilityPercent is not { } availability)
        {
            return "Availability needs a run with a measured duration.";
        }

        var dropouts = stability.Episodes.Count(episode => episode.Kind == StabilityEventKind.LossBurst);
        return dropouts == 0
            ? $"No dropouts over {stability.Window.TotalSeconds:0} s — the line answered the whole time. "
                + "Leave it running: a hole that opens twice a day is invisible in a thirty-second test."
            : $"{availability:0.00}% availability over {stability.Window.TotalSeconds:0} s across {dropouts} dropout(s). "
                + "That figure is a share of time, not of packets, and its resolution is the probe interval.";
    }

    /// <summary>
    /// Restates a verdict as findings, so an imported capture and a live measurement reach the
    /// dashboard through the same door and with the same thresholds behind them.
    /// </summary>
    public static IReadOnlyList<HealthFinding> Findings(PlayabilityVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        return verdict.Metrics
            .Where(metric => metric.Grade is PlayabilityGrade.Playable or PlayabilityGrade.Poor)
            .Select(metric => new HealthFinding(
                $"{verdict.Game.DisplayName}: {metric.Name} is {(metric.Grade == PlayabilityGrade.Poor ? "past" : "at")} "
                    + $"what a {verdict.Game.TickRateHz:0.#} Hz tick allows",
                $"{metric.Measured} against a budget of {metric.Budget}.",
                metric.Explanation,
                "Gaming diagnostics",
                metric.Grade == PlayabilityGrade.Poor ? DiagnosticConfidence.High : DiagnosticConfidence.Medium,
                metric.Grade == PlayabilityGrade.Poor ? ChangeRisk.High : ChangeRisk.Medium))
            .ToArray();
    }

    /// <summary>
    /// Ranks grades for "the worst one decides". A metric that could not be measured ranks below
    /// every measured one: one unusable jitter figure must not turn a clean run into a bad verdict.
    /// </summary>
    private static int Severity(PlayabilityGrade grade) => grade switch
    {
        PlayabilityGrade.Poor => 3,
        PlayabilityGrade.Playable => 2,
        PlayabilityGrade.Good => 1,
        _ => 0
    };

    private static MetricVerdict JudgeLatency(ProbeStatistics statistics, GameProfile game)
    {
        var (good, playable) = game.PingBudgetMs;
        var median = statistics.MedianMs ?? 0;
        var grade = median <= good ? PlayabilityGrade.Good
            : median <= playable ? PlayabilityGrade.Playable
            : PlayabilityGrade.Poor;

        return new MetricVerdict(
            "latency",
            grade,
            $"{median:0.0} ms median",
            $"{good:0} ms good, {playable:0} ms playable",
            grade == PlayabilityGrade.Good
                ? $"{median:0.0} ms is inside the budget a {game.TickRateHz:0.#} Hz game leaves for the round trip."
                : $"{median:0.0} ms is {median / game.TickIntervalMs:0.#} server updates of round trip at "
                    + $"{game.TickRateHz:0.#} Hz. Distance and route set most of that number and no local setting moves it — "
                    + "check whether the path shape explains it before changing anything on this machine.");
    }

    private static MetricVerdict JudgeJitter(ProbeStatistics statistics, GameProfile game)
    {
        // The windowed figure is preferred because it does not depend on how fast the probe sent:
        // consecutive samples are 100 ms apart on one profile and 500 ms apart on another, and the
        // consecutive-difference jitter would report a different number for the same line.
        var windowed = statistics.WindowedJitterMs;
        var jitter = windowed ?? statistics.JitterMs;
        var method = windowed is null
            ? "consecutive-difference jitter (too few samples per second for the windowed figure)"
            : "jitter over fixed one-second windows";

        if (jitter is not { } value)
        {
            return new MetricVerdict("jitter", PlayabilityGrade.Unmeasured, "not measurable",
                $"{game.GoodJitterMs:0.0} ms good, {game.PlayableJitterMs:0.0} ms playable",
                "Two successful replies are needed before jitter means anything.");
        }

        var grade = value <= game.GoodJitterMs ? PlayabilityGrade.Good
            : value <= game.PlayableJitterMs ? PlayabilityGrade.Playable
            : PlayabilityGrade.Poor;

        var quantisation = game.GoodJitterMs < QuantisationFloorMs
            ? " Windows reports an ICMP round trip in whole milliseconds, so at this tick rate the measurement's own "
                + "resolution is a noticeable share of the budget — read the grade as a direction, not a decimal."
            : string.Empty;

        return new MetricVerdict(
            "jitter",
            grade,
            $"{value:0.0} ms, {method}",
            $"{game.GoodJitterMs:0.0} ms good, {game.PlayableJitterMs:0.0} ms playable",
            (grade == PlayabilityGrade.Good
                ? $"{value:0.0} ms stays inside half of the {game.TickIntervalMs:0.0} ms between server updates, so "
                    + "inputs land in the tick they were meant for."
                : $"Jitter is {value:0.0} ms while the server updates every {game.TickIntervalMs:0.0} ms. Inputs do not "
                    + "land in a predictable update, which is what is felt as hits registering late. Measure the same "
                    + "endpoint again under load: jitter that only appears under load is a queue, and the queue is the "
                    + "router's to fix.") + quantisation);
    }

    private static MetricVerdict JudgeLoss(ProbeStatistics statistics)
    {
        var loss = statistics.LossPercent;
        var grade = loss <= 0 ? PlayabilityGrade.Good
            : loss <= PlayableLossPercent ? PlayabilityGrade.Playable
            : PlayabilityGrade.Poor;

        return new MetricVerdict(
            "packet loss",
            grade,
            $"{loss:0.##}% ({statistics.Lost} of {statistics.Sent})",
            "0% good, 1% playable",
            grade == PlayabilityGrade.Good
                ? "Every probe came back. Nothing on this path dropped one while the test ran."
                : $"{statistics.Lost} probe(s) of {statistics.Sent} went missing. A game does not resend a lost update — "
                    + "by the time it arrived it would describe where you used to be — so each one is an input that never "
                    + "happened. A round trip cannot say in which direction it was dropped, and this threshold is the same "
                    + "at every tick rate because a lost packet costs the same everywhere.");
    }
}
