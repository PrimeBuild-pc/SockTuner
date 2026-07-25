using SockTuner.Models;

namespace SockTuner.Services;

public static class AdapterCounterDeltaCalculator
{
    public static IReadOnlyList<AdapterCounterDelta> Calculate(NetworkSnapshot before, NetworkSnapshot after) => Calculate(
        before.Adapters.Where(adapter => adapter.Counters is not null)
            .Select(adapter => new AdapterCounterSample(adapter.Id, adapter.Name, adapter.Counters!)).ToArray(),
        after.Adapters.Where(adapter => adapter.Counters is not null)
            .Select(adapter => new AdapterCounterSample(adapter.Id, adapter.Name, adapter.Counters!)).ToArray());

    public static IReadOnlyList<AdapterCounterDelta> Calculate(
        IReadOnlyList<AdapterCounterSample> before,
        IReadOnlyList<AdapterCounterSample> after) => before
            .Select(sample => (Before: sample, After: after.FirstOrDefault(candidate =>
                string.Equals(candidate.AdapterId, sample.AdapterId, StringComparison.OrdinalIgnoreCase))))
            .Where(pair => pair.After is not null)
            .Select(pair => new AdapterCounterDelta(
                pair.Before.AdapterId,
                pair.Before.AdapterName,
                Delta(pair.Before.Counters.BytesReceived, pair.After!.Counters.BytesReceived),
                Delta(pair.Before.Counters.BytesSent, pair.After.Counters.BytesSent),
                Delta(pair.Before.Counters.IncomingPacketsWithErrors, pair.After.Counters.IncomingPacketsWithErrors),
                Delta(pair.Before.Counters.IncomingPacketsDiscarded, pair.After.Counters.IncomingPacketsDiscarded),
                Delta(pair.Before.Counters.OutgoingPacketsWithErrors, pair.After.Counters.OutgoingPacketsWithErrors),
                Delta(pair.Before.Counters.OutgoingPacketsDiscarded, pair.After.Counters.OutgoingPacketsDiscarded)))
            .ToArray();

    internal static long? Delta(long before, long after) => after >= before ? after - before : null;
}
