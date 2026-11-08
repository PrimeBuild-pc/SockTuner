using SockTuner.Models;

namespace SockTuner.Services.Diagnosis;

public static class UdpQualityComparison
{
    public static ApplyVerification Compare(UdpQualityReport before, UdpQualityReport after, bool sameConditions)
    {
        if (!sameConditions || before.Endpoint != after.Endpoint || before.Options != after.Options
            || before.AdapterId.Length == 0 || before.AdapterId != after.AdapterId
            || before.LocalAddress != after.LocalAddress || before.LinkSpeed != after.LinkSpeed || before.Wireless != after.Wireless)
            return new(ApplyOutcome.NotComparable, "These UDP runs cannot be compared",
                "Use the same responder, options, interface, local address and link speed, and confirm the same background traffic and Wi-Fi position/association.", []);
        if (before.Received < 10 || after.Received < 10 || before.SchedulingDelays > before.Sent / 20
            || after.SchedulingDelays > after.Sent / 20)
            return new(ApplyOutcome.NotComparable, "Insufficient stable UDP measurements",
                "At least ten echoes per run and no more than 5% locally delayed sends are required. Local scheduling delays are not network latency.", []);
        var floor = Math.Max(1, Math.Max(before.JitterMs ?? 1, after.JitterMs ?? 1));
        var result = ApplyVerificationAnalyzer.FromMetrics(
        [
            new("UDP median RTT", before.MedianMs, after.MedianMs, floor, "ms"),
            new("UDP P95 RTT", before.P95Ms, after.P95Ms, floor, "ms"),
            new("UDP RTT jitter", before.JitterMs, after.JitterMs, floor, "ms"),
            new("Upload loss", before.UploadLossPercent, after.UploadLossPercent, 100d / before.Sent, "%"),
            new("Download loss", before.DownloadLossPercent, after.DownloadLossPercent,
                Math.Max(100d / before.ServerReceived, 100d / after.ServerReceived), "%"),
            new("Late echoes", 100d * before.Late / before.Sent, 100d * after.Late / after.Sent, 100d / before.Sent, "%")
        ]);
        return result with { Detail = result.Detail + " This is an observed comparison with heuristic noise floors, not a statistical certification or proof that the preset caused the difference. Repeat with the same load; check CPU and throughput separately for offload changes." };
    }
}
