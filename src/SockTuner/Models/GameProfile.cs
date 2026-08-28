namespace SockTuner.Models;

/// <summary>How much weight the tick rate of a profile carries.</summary>
public enum TickRateSource
{
    /// <summary>The developer has stated the rate publicly.</summary>
    Published,

    /// <summary>Widely reported and consistent with capture work, but not a developer figure.</summary>
    Community,

    /// <summary>A band, not a title-specific number.</summary>
    Generic
}

/// <summary>
/// A game's network cadence: how often its server advances the world, and roughly how large the
/// packets that carry it are.
/// </summary>
/// <remarks>
/// <para>
/// The tick rate is the only number that makes a jitter figure mean anything. Eight milliseconds of
/// jitter is a rounding error against a 50 ms tick and is most of the budget against a 7.8 ms one,
/// so every threshold in <c>PlayabilityAnalyzer</c> is derived from this value rather than chosen.
/// </para>
/// <para>
/// The rate is what SockTuner judges against; it is deliberately not what SockTuner sends at. A
/// serial ICMP probe cannot reach 128 packets per second against an 18 ms path, and reproducing a
/// game's send rate against somebody else's server is not something a diagnostic tool should do
/// uninvited. The measurement stays spaced and modest, and the rate-independent jitter figure is
/// what keeps the comparison valid.
/// </para>
/// </remarks>
public sealed record GameProfile(
    string Id,
    string DisplayName,
    double TickRateHz,
    TickRateSource Source,
    string Evidence)
{
    /// <summary>The interval between two server updates: the window every threshold is scaled to.</summary>
    public double TickIntervalMs => 1000d / TickRateHz;

    /// <summary>
    /// Payload size for SockTuner's own probe, taken from the tick band rather than measured per
    /// title. It approximates the traffic shape — a live game packet is tens to a few hundred bytes
    /// — and it changes nothing but the size of the packets this app sends.
    /// </summary>
    public int PayloadBytes => TickRateHz switch
    {
        >= 100 => 200,
        >= 50 => 150,
        >= 25 => 120,
        _ => 100
    };

    /// <summary>Jitter below this is invisible: it stays inside half a server update.</summary>
    /// <remarks>
    /// Capped, because the tick arithmetic alone would call 90 ms of jitter fine on a 10 Hz game.
    /// That is true of the tick and useless to a person.
    /// </remarks>
    public double GoodJitterMs => Math.Min(TickIntervalMs / 2, 15);

    /// <summary>Jitter beyond one full tick means an input can no longer be placed in a predictable update.</summary>
    public double PlayableJitterMs => Math.Min(TickIntervalMs, 30);

    /// <summary>
    /// Latency budget in milliseconds. Unlike the jitter limits this is a judgement rather than
    /// arithmetic, and it is stated as one: a faster tick resolves smaller differences, so the same
    /// round trip costs more of them.
    /// </summary>
    public (double Good, double Playable) PingBudgetMs => TickRateHz switch
    {
        >= 100 => (30, 60),
        >= 50 => (40, 80),
        >= 25 => (60, 100),
        _ => (80, 150)
    };

    public string TickDisplay => $"{TickRateHz:0.#} Hz — one update every {TickIntervalMs:0.0} ms";

    public string SourceDisplay => Source switch
    {
        TickRateSource.Published => "developer-stated",
        TickRateSource.Community => "community-reported",
        _ => "a band, not a title"
    };

    /// <summary>A rate the user typed, for a title the catalogue does not carry.</summary>
    public static GameProfile Custom(double tickRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(tickRateHz, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tickRateHz, 1000);
        return new GameProfile(
            "custom",
            $"Custom ({tickRateHz:0.#} Hz)",
            tickRateHz,
            TickRateSource.Generic,
            "Entered by hand. Nothing here verifies it, so the verdict is only as good as the number.");
    }

    /// <summary>Turns a tick interval from an imported capture back into a profile to judge against.</summary>
    public static GameProfile FromTickIntervalMs(string game, double tickIntervalMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(tickIntervalMs, 0);
        var hz = 1000d / tickIntervalMs;
        return GameProfiles.ClosestTo(hz)
            ?? new GameProfile(
                "imported",
                game,
                hz,
                TickRateSource.Generic,
                $"Taken from the imported capture report, which stated a {tickIntervalMs:0.#} ms tick.");
    }
}

/// <summary>
/// The tick rates SockTuner judges against. Every entry says where its number came from, because a
/// threshold derived from a wrong tick rate is worse than no threshold: it is confidently wrong.
/// </summary>
public static class GameProfiles
{
    public static IReadOnlyList<GameProfile> All { get; } =
    [
        new("pro-esports", "Pro esports (128 Hz)", 128, TickRateSource.Generic,
            "The band competitive shooters run their ranked servers at. Use it when the title is not listed."),
        new("valorant", "Valorant", 128, TickRateSource.Published,
            "Riot has stated its Valorant servers run at 128 ticks per second."),
        new("rocket-league", "Rocket League", 120, TickRateSource.Community,
            "Reported as a 120 Hz physics and send rate. Not a developer statement."),
        new("cs2", "Counter-Strike 2", 64, TickRateSource.Published,
            "Valve's official servers send 64 updates per second. CS2's sub-tick input timing changes when an "
            + "input is registered, not how often the server updates."),
        new("standard-shooter", "Standard shooter (64 Hz)", 64, TickRateSource.Generic,
            "The band most non-esports shooters run at. Use it when the title is not listed."),
        new("overwatch-2", "Overwatch 2", 63, TickRateSource.Community,
            "Reported as a 63 Hz update rate on the high-bandwidth setting. Not confirmed by Blizzard for the current build."),
        new("r6-siege", "Rainbow Six Siege", 60, TickRateSource.Published,
            "Ubisoft moved Siege to 60-tick servers and said so."),
        new("battlefield-2042", "Battlefield 2042", 60, TickRateSource.Community,
            "Reported as 60 Hz on official servers. Not a developer statement."),
        new("fortnite", "Fortnite", 30, TickRateSource.Published,
            "Epic's servers tick at 30 Hz; competitive playlists use the same rate."),
        new("league-of-legends", "League of Legends", 30, TickRateSource.Community,
            "Widely reported as 30 server ticks per second. Not a developer statement."),
        new("casual", "Casual / battle royale (30 Hz)", 30, TickRateSource.Generic,
            "The band most battle royales and large-map shooters run at."),
        new("apex-legends", "Apex Legends", 20, TickRateSource.Published,
            "Respawn has stated Apex servers run at 20 Hz."),
        new("warzone", "Call of Duty: Warzone", 20, TickRateSource.Community,
            "Reported as 20 Hz on battle royale servers; smaller playlists run higher. Not a developer statement."),
        new("mmo", "MMO / survival (20 Hz)", 20, TickRateSource.Generic,
            "The band MMOs and survival games typically run at, Minecraft's Java server among them.")
    ];

    public static GameProfile Get(string id) =>
        All.FirstOrDefault(profile => string.Equals(profile.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Unknown game profile: {id}");

    /// <summary>
    /// Finds the named title whose tick rate is closest to a measured or imported one, so an
    /// imported capture lands on a profile with evidence attached instead of a bare number. Generic
    /// bands are excluded: matching one would claim a title where there is none.
    /// </summary>
    public static GameProfile? ClosestTo(double tickRateHz) => tickRateHz <= 0
        ? null
        : All.Where(profile => profile.Source != TickRateSource.Generic)
            .OrderBy(profile => Math.Abs(profile.TickRateHz - tickRateHz))
            .FirstOrDefault(profile => Math.Abs(profile.TickRateHz - tickRateHz) <= 2);
}
