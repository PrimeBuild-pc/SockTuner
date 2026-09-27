using System.Diagnostics;
using SockTuner.Models;
using SockTuner.Services.Collection;

namespace SockTuner.Services.Diagnosis;

/// <summary>Short, cancellable observation of the current association; never triggers a WLAN scan.</summary>
public sealed class WifiStabilityObserver
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    public async Task<IReadOnlyList<WifiObservationSample>> ObserveAsync(
        string interfaceId,
        TimeSpan duration,
        TimeSpan interval,
        IProgress<WifiObservationSample>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(interfaceId)) throw new ArgumentException("An interface ID is required.", nameof(interfaceId));
        if (duration <= TimeSpan.Zero || duration > MaximumDuration) throw new ArgumentOutOfRangeException(nameof(duration));
        if (interval < MinimumInterval || interval > duration) throw new ArgumentOutOfRangeException(nameof(interval));

        var samples = new List<WifiObservationSample>();
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sample = await Task.Run(() => WindowsWifiInventory.ReadConnectionSample(interfaceId), cancellationToken);
            samples.Add(sample);
            progress?.Report(sample);
            if (elapsed.Elapsed + interval >= duration) break;
            await Task.Delay(interval, cancellationToken);
        }

        return samples;
    }
}
