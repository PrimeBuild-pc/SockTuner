using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

/// <summary>
/// Diagnosis layer: finds the episodes inside a long monitoring run. A spot test reports an
/// average; the fault users actually live with is a two-minute hole every twenty minutes, and only
/// a long window with episode detection can show it.
/// </summary>
public static class StabilityAnalyzer
{
    /// <summary>A single bad sample is noise. Two consecutive ones are an event with a start and an end.</summary>
    private const int MinimumEpisodeSamples = 2;

    /// <summary>Floor on what counts as a spike, so a stable sub-millisecond LAN does not generate episodes.</summary>
    private const double MinimumSpikeMs = 20;

    private const double SpikeJitterMultiplier = 3;

    public static StabilityReport Analyze(MonitorReport report)
    {
        var episodes = new List<StabilityEpisode>();
        foreach (var group in report.Samples.GroupBy(sample => (sample.Label, sample.Target)))
        {
            var ordered = group.OrderBy(sample => sample.Timestamp).ToArray();
            episodes.AddRange(EpisodesFor(group.Key.Label, group.Key.Target, ordered));
        }

        return new StabilityReport(
            report.Duration,
            episodes.OrderBy(episode => episode.StartedAt).ToArray(),
            report.Summaries,
            report.SamplesTruncated,
            Verdict(report, episodes));
    }

    private static IEnumerable<StabilityEpisode> EpisodesFor(string label, string target, IReadOnlyList<MonitorSample> samples)
    {
        var replies = samples.Where(sample => sample.RoundTripTimeMs.HasValue)
            .Select(sample => new ProbeSample(sample.Timestamp, sample.RoundTripTimeMs))
            .ToArray();
        var baseline = ProbeStatistics.Calculate(label, target, replies);

        // The run is graded against itself: a 90 ms path is not a spike, a 90 ms path jumping to
        // 400 ms is. Without a baseline of its own there is nothing to call a spike against.
        var spikeThreshold = baseline.MedianMs is { } median
            ? median + Math.Max(MinimumSpikeMs, (baseline.JitterMs ?? 0) * SpikeJitterMultiplier)
            : double.MaxValue;

        StabilityEventKind? current = null;
        var start = 0;
        for (var index = 0; index <= samples.Count; index++)
        {
            var kind = index == samples.Count ? null : Classify(samples[index], spikeThreshold);
            if (kind == current)
            {
                continue;
            }

            if (current is { } ending && index - start >= MinimumEpisodeSamples)
            {
                var window = samples.Skip(start).Take(index - start).ToArray();
                yield return new StabilityEpisode(
                    ending, label, target,
                    window[0].Timestamp,
                    window[^1].Timestamp,
                    window.Length,
                    window.Max(sample => sample.RoundTripTimeMs));
            }

            current = kind;
            start = index;
        }
    }

    // A local API failure says nothing about the path, so it is never counted as loss on it.
    private static StabilityEventKind? Classify(MonitorSample sample, double spikeThreshold) => sample.Kind switch
    {
        MonitorSampleKind.Reply => sample.RoundTripTimeMs > spikeThreshold ? StabilityEventKind.LatencySpike : null,
        MonitorSampleKind.LocalError => null,
        _ => StabilityEventKind.LossBurst
    };

    private static string Verdict(MonitorReport report, IReadOnlyList<StabilityEpisode> episodes)
    {
        var truncation = report.SamplesTruncated
            ? " Older samples were dropped from the in-memory window, so earlier episodes may be missing."
            : string.Empty;
        if (episodes.Count == 0)
        {
            return $"No loss burst or latency spike over {report.Duration.TotalMinutes:0.#} minute(s). "
                + "A stable window does not rule out a fault outside it." + truncation;
        }

        var loss = episodes.Count(episode => episode.Kind == StabilityEventKind.LossBurst);
        return $"{episodes.Count} episode(s) over {report.Duration.TotalMinutes:0.#} minute(s): "
            + $"{loss} loss burst(s), {episodes.Count - loss} latency spike(s)." + truncation;
    }
}
