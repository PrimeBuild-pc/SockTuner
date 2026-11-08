namespace SockTuner.Models;

public sealed record UdpQualityOptions(int DurationSeconds = 30, int PacketsPerSecond = 20,
    int PacketBytes = 128, int LateThresholdMs = 100)
{
    public int PacketCount => DurationSeconds * PacketsPerSecond;
    public void Validate()
    {
        if (DurationSeconds is < 1 or > 60 || PacketsPerSecond is < 1 or > 50
            || PacketCount > 1200 || PacketBytes is < 96 or > 1200 || LateThresholdMs is < 1 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(DurationSeconds), "Use 1–60 seconds, 1–50 packets/s, at most 1200 packets, 96–1200 bytes and a 1–2000 ms late threshold.");
    }
}

public sealed record UdpQualityProgress(int Sent, int Total);

public sealed record UdpQualityReport(DateTimeOffset StartedAt, string Endpoint, UdpQualityOptions Options,
    string LocalAddress, string AdapterId, long LinkSpeed, bool Wireless,
    int Sent, int ServerReceived, int Received, int Late, int Duplicates, int Reordered,
    int SchedulingDelays, double ElapsedSeconds, IReadOnlyList<double> RoundTripsMs)
{
    public int UploadLost => Sent - ServerReceived;
    public int DownloadLost => ServerReceived - Received;
    public double UploadLossPercent => Sent == 0 ? 0 : 100d * UploadLost / Sent;
    public double? DownloadLossPercent => ServerReceived == 0 ? null : 100d * DownloadLost / ServerReceived;
    public double? MedianMs => Percentile(0.5);
    public double? P95Ms => Percentile(0.95);
    public double? JitterMs => RoundTripsMs.Count < 2 ? null
        : RoundTripsMs.Zip(RoundTripsMs.Skip(1), (a, b) => Math.Abs(b - a)).Average();
    public string JitterMethod => "Mean absolute consecutive echo RTT difference, in receive order";
    public string MeasurementScope => "Authenticated UDP echo RTT; direction-specific loss from the responder receipt bitmap. Not one-way latency or a measurement of the game's server.";

    private double? Percentile(double p)
    {
        if (RoundTripsMs.Count == 0) return null;
        var values = RoundTripsMs.Order().ToArray();
        var position = (values.Length - 1) * p;
        var lower = (int)position;
        var upper = Math.Min(lower + 1, values.Length - 1);
        return values[lower] + (values[upper] - values[lower]) * (position - lower);
    }
}
