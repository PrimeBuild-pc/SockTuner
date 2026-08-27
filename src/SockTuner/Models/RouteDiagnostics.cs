namespace SockTuner.Models;

/// <summary>Where an address sits relative to the user's control.</summary>
public enum HopAddressKind
{
    Unknown,

    /// <summary>RFC1918 or link-local: inside the user's own network.</summary>
    Private,

    /// <summary>RFC6598 (100.64.0.0/10): the ISP is carrier-grade NAT-ing this connection.</summary>
    CarrierGrade,

    Public
}

/// <summary>
/// The chain a packet crosses, ordered from the machine outwards. Localisation walks this in
/// order and reports the first place quality degrades.
/// </summary>
public enum NetworkSegment
{
    Unknown,
    LocalNicDriver,
    Lan,
    RouterOrAccess,
    IspAccess,
    IspCore,
    ExternalHop,
    RemoteEndpoint
}

/// <summary>Who has to act on a finding. Derived, never written by hand per finding.</summary>
public enum RemediationOwner
{
    /// <summary>Safe, reversible, no user choice: SockTuner can apply it itself.</summary>
    Automatic,

    /// <summary>Needs a target value or a judgement call from the user.</summary>
    PresetOrManual,

    /// <summary>Needs router configuration, optionally applied over SSH on OpenWrt.</summary>
    Router,

    /// <summary>ISP or infrastructure limit: diagnose and explain, never attempt a fix.</summary>
    OutOfScope
}

/// <summary>How much of the lever SockTuner actually holds for a given problem.</summary>
public enum LocalControl
{
    /// <summary>SockTuner cannot change anything relevant.</summary>
    None,

    /// <summary>A reversible, low-risk change with no user decision to make.</summary>
    AutomaticSafe,

    /// <summary>SockTuner can change it, but the value or trade-off is the user's call.</summary>
    RequiresChoice
}

/// <summary>
/// One hop aggregated across several traceroute rounds. <paramref name="RoundsObserved"/> counts
/// rounds where the trace reached this TTL at all, which is what makes a non-responding hop
/// distinguishable from a hop the trace never got to.
/// </summary>
public sealed record HopMeasurement(
    int TimeToLive,
    string Address,
    HopAddressKind AddressKind,
    ProbeStatistics Statistics,
    int RoundsObserved,
    int RoundsResponded,
    IReadOnlyList<string> AlternateAddresses)
{
    public bool Responded => RoundsResponded > 0;
    public double LossPercent => RoundsObserved == 0
        ? 0
        : (RoundsObserved - RoundsResponded) * 100d / RoundsObserved;
    public bool IsUnstable => AlternateAddresses.Count > 0;
    public string AddressDisplay => Responded ? Address : "* (no reply)";
    public string Summary => Responded
        ? $"{TimeToLive}. {Address} — {Statistics.Summary}"
        : $"{TimeToLive}. * — no reply in {RoundsObserved} round(s)";
}

/// <summary>Repeated multi-hop sampling of one path, in the spirit of mtr rather than a single traceroute.</summary>
public sealed record RoutePathDiagnostic(
    string Target,
    DateTimeOffset StartedAt,
    int Rounds,
    IReadOnlyList<HopMeasurement> Hops,
    bool ReachedTarget,
    string? Error,
    DiagnosticFailureKind? FailureKind = null)
{
    public IReadOnlyList<HopMeasurement> RespondingHops => Hops.Where(hop => hop.Responded).ToArray();

    /// <summary>The first hop outside the user's own network, i.e. the ISP-facing boundary.</summary>
    public HopMeasurement? FirstPublicHop => Hops
        .FirstOrDefault(hop => hop.Responded && hop.AddressKind is HopAddressKind.Public);

    /// <summary>
    /// Present when the ISP places the connection behind carrier-grade NAT. That cannot be fixed
    /// from the endpoint or the router and is a common cause of gaming and P2P problems.
    /// </summary>
    public HopMeasurement? CarrierGradeNatHop => Hops
        .FirstOrDefault(hop => hop.Responded && hop.AddressKind is HopAddressKind.CarrierGrade);

    public bool HasUnstableRouting => Hops.Any(hop => hop.IsUnstable);

    /// <summary>
    /// Hops whose loss does not continue at the hops beyond them. Routers commonly rate-limit or
    /// deprioritise ICMP addressed to themselves while forwarding traffic perfectly, so loss that
    /// stops at one hop is an artefact of the measurement, not a fault on the path.
    /// </summary>
    public IReadOnlyList<HopMeasurement> RateLimitedHops
    {
        get
        {
            var responding = Hops.Where(hop => hop.RoundsObserved > 0).ToArray();
            return responding
                .Where((hop, index) => hop.LossPercent > 0 && !DegradationPersistsAfter(responding, index))
                .ToArray();
        }
    }

    /// <summary>
    /// Hops where loss begins and continues downstream. Only these are candidates for a real fault.
    /// </summary>
    /// <remarks>
    /// A hop downstream of a lossy one inherits that loss, so its absolute figure is never lower.
    /// Counting every small increase would report one fault several times over; a new fault has to
    /// add substantially more than the loss already arriving at that hop.
    /// </remarks>
    public IReadOnlyList<HopMeasurement> PersistentLossHops
    {
        get
        {
            var responding = Hops.Where(hop => hop.RoundsObserved > 0).ToArray();
            var result = new List<HopMeasurement>();
            for (var index = 0; index < responding.Length; index++)
            {
                var inheritedLoss = index == 0 ? 0 : responding[index - 1].LossPercent;
                if (responding[index].LossPercent > inheritedLoss + NewFaultLossPercent
                    && DegradationPersistsAfter(responding, index))
                {
                    result.Add(responding[index]);
                }
            }

            return result;
        }
    }

    // How much loss a hop must add over what already reaches it before it counts as a new fault
    // rather than the same fault seen again further along.
    private const double NewFaultLossPercent = 15;

    // Tolerance for sampling noise when checking that loss really does continue downstream.
    private const double DownstreamTolerancePercent = 5;

    // Loss is only believable if every later hop is at least as bad; a single clean hop after it
    // proves the packets were getting through all along.
    private static bool DegradationPersistsAfter(IReadOnlyList<HopMeasurement> hops, int index)
    {
        var loss = hops[index].LossPercent;
        var later = hops.Skip(index + 1).Where(hop => hop.RoundsObserved > 0).ToArray();
        return later.Length > 0 && later.All(hop => hop.LossPercent >= loss - DownstreamTolerancePercent);
    }
}

/// <summary>Where the chain first degrades, with the observations for and against that call.</summary>
public sealed record BottleneckAssessment(
    NetworkSegment Segment,
    DiagnosticConfidence Confidence,
    RemediationOwner Owner,
    string Title,
    IReadOnlyList<string> Supporting,
    IReadOnlyList<string> Contradicting)
{
    public bool IsConclusive => Segment != NetworkSegment.Unknown;
    public string SupportingDisplay => Supporting.Count == 0 ? "—" : string.Join(" · ", Supporting);
    public string ContradictingDisplay => Contradicting.Count == 0 ? "—" : string.Join(" · ", Contradicting);
}

/// <summary>Local link facts folded into localisation so a local fault is not blamed on the ISP.</summary>
public sealed record LocalLinkEvidence(
    bool LinkUp,
    long SpeedBitsPerSecond,
    long ReceiveErrors,
    long ReceiveDiscards,
    long TransmitErrors,
    long TransmitDiscards,
    bool IsWireless)
{
    public long TotalErrors => ReceiveErrors + ReceiveDiscards + TransmitErrors + TransmitDiscards;

    public static LocalLinkEvidence Healthy => new(true, 1_000_000_000, 0, 0, 0, 0, false);
}
