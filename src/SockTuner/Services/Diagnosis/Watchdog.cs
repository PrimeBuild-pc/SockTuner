using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

public enum WatchdogAlertKind
{
    Latency,
    Loss
}

/// <summary>
/// A threshold that stayed crossed across a whole window. <see cref="StartedAt"/> and
/// <see cref="EndedAt"/> bracket the samples that were actually bad, not the moments the alert was
/// raised and cleared — "when did this start" is the question the user asks, and the answer is
/// always earlier than the alert.
/// </summary>
public sealed record WatchdogAlert(
    WatchdogAlertKind Kind,
    string Label,
    string Target,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    double Measured,
    double Threshold)
{
    public bool Open => EndedAt is null;
    public TimeSpan? Duration => EndedAt - StartedAt;

    public string Summary => Kind == WatchdogAlertKind.Latency
        ? $"{Label}: median latency {Measured:0.#} ms over a {Threshold:0.#} ms threshold, from {StartedAt:t}"
            + (EndedAt is { } ended ? $" to {ended:t}" : " and still open")
        : $"{Label}: {Measured:0.#}% of probes unanswered against a {Threshold:0.#}% threshold, from {StartedAt:t}"
            + (EndedAt is { } lossEnded ? $" to {lossEnded:t}" : " and still open");
}

public sealed record WatchdogThresholds(
    double MaximumLatencyMs = 100,
    double MaximumLossPercent = 5,
    int WindowSamples = 20)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumLatencyMs);
        if (MaximumLossPercent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(MaximumLossPercent));
        ArgumentOutOfRangeException.ThrowIfLessThan(WindowSamples, 3);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(WindowSamples, 1000);
    }
}

/// <summary>
/// Diagnosis layer over a live sample stream: raises an alert when a threshold stays crossed across
/// a whole window, and closes it when the window recovers. Judging on a rolling window rather than
/// on single samples is the point — one lost probe is not an outage, and a user woken by every one
/// of them stops reading the alerts.
/// </summary>
/// <remarks>
/// Holds one bounded window per target plus a bounded alert list, so it can run for hours behind
/// <c>NetworkMonitorService</c> without growing.
/// </remarks>
public sealed class Watchdog
{
    private const int MaximumRetainedAlerts = 50;

    private readonly WatchdogThresholds _thresholds;
    private readonly Dictionary<(string Label, string Target), TargetWindow> _windows = [];
    private readonly List<WatchdogAlert> _alerts = [];

    public Watchdog(WatchdogThresholds thresholds)
    {
        thresholds.Validate();
        _thresholds = thresholds;
    }

    public IReadOnlyList<WatchdogAlert> Alerts => _alerts;
    public IReadOnlyList<WatchdogAlert> OpenAlerts => _alerts.Where(alert => alert.Open).ToArray();

    /// <summary>Feeds one sample in. Returns an alert when this sample opened or closed one.</summary>
    public WatchdogAlert? Observe(MonitorSample sample)
    {
        // A local API failure says nothing about the path, so it neither counts as loss nor resets
        // a window that is already bad.
        if (sample.Kind == MonitorSampleKind.LocalError)
        {
            return null;
        }

        var key = (sample.Label, sample.Target);
        if (!_windows.TryGetValue(key, out var window))
        {
            window = _windows[key] = new TargetWindow(_thresholds.WindowSamples);
        }

        window.Add(sample);
        if (!window.Full)
        {
            return null;
        }

        var latency = window.MedianLatencyMs;
        var loss = window.LossPercent;
        var breach = loss > _thresholds.MaximumLossPercent
            ? WatchdogAlertKind.Loss
            : latency > _thresholds.MaximumLatencyMs ? WatchdogAlertKind.Latency : (WatchdogAlertKind?)null;

        if (breach is { } kind)
        {
            return window.Open is null ? Open(key, window, kind, latency, loss) : null;
        }

        return window.Open is null ? null : Close(window, sample);
    }

    private WatchdogAlert Open(
        (string Label, string Target) key, TargetWindow window, WatchdogAlertKind kind, double? latency, double loss)
    {
        var alert = new WatchdogAlert(
            kind, key.Label, key.Target,
            window.FirstBadTimestamp(kind, _thresholds),
            null,
            kind == WatchdogAlertKind.Loss ? loss : latency ?? 0,
            kind == WatchdogAlertKind.Loss ? _thresholds.MaximumLossPercent : _thresholds.MaximumLatencyMs);
        window.Open = alert;
        _alerts.Add(alert);
        if (_alerts.Count > MaximumRetainedAlerts)
        {
            _alerts.RemoveAt(0);
        }

        return alert;
    }

    private WatchdogAlert Close(TargetWindow window, MonitorSample sample)
    {
        // Dated from the last sample that was itself bad, mirroring how it was opened: the alert is
        // the stretch that was actually bad, not the stretch it took to notice and to recover.
        var closed = window.Open! with
        {
            EndedAt = window.LastBadTimestamp(window.Open.Kind, _thresholds) ?? sample.Timestamp
        };
        var index = _alerts.IndexOf(window.Open);
        if (index >= 0)
        {
            _alerts[index] = closed;
        }

        window.Open = null;
        return closed;
    }

    private sealed class TargetWindow(int capacity)
    {
        private readonly Queue<MonitorSample> _samples = new(capacity);

        public WatchdogAlert? Open { get; set; }
        public bool Full => _samples.Count == capacity;

        public void Add(MonitorSample sample)
        {
            if (_samples.Count == capacity)
            {
                _samples.Dequeue();
            }

            _samples.Enqueue(sample);
        }

        public double LossPercent => _samples.Count == 0
            ? 0
            : _samples.Count(sample => sample.Kind != MonitorSampleKind.Reply) * 100d / _samples.Count;

        public double? MedianLatencyMs
        {
            get
            {
                var values = _samples.Where(sample => sample.RoundTripTimeMs.HasValue)
                    .Select(sample => sample.RoundTripTimeMs!.Value).Order().ToArray();
                return values.Length == 0 ? null : values[values.Length / 2];
            }
        }

        /// <summary>
        /// The oldest sample in the window that was itself bad. The alert fires once the window's
        /// median crosses the threshold, but the problem started at the first sample that was bad.
        /// </summary>
        public DateTimeOffset FirstBadTimestamp(WatchdogAlertKind kind, WatchdogThresholds thresholds) =>
            (Bad(kind, thresholds).FirstOrDefault() ?? _samples.Peek()).Timestamp;

        /// <summary>
        /// The newest still-remembered bad sample. Null once they have all aged out of the window,
        /// which the caller answers with the sample that closed the alert.
        /// </summary>
        public DateTimeOffset? LastBadTimestamp(WatchdogAlertKind kind, WatchdogThresholds thresholds) =>
            Bad(kind, thresholds).LastOrDefault()?.Timestamp;

        private IEnumerable<MonitorSample> Bad(WatchdogAlertKind kind, WatchdogThresholds thresholds) =>
            _samples.Where(sample => kind == WatchdogAlertKind.Loss
                ? sample.Kind != MonitorSampleKind.Reply
                : sample.RoundTripTimeMs > thresholds.MaximumLatencyMs);
    }
}
