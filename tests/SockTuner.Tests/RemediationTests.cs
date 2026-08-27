using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Remediation;

namespace SockTuner.Tests;

/// <summary>
/// Remediation proposes; it never decides that there is a problem and never invents a lever. Every
/// case here runs against fake capabilities and an in-memory store, so nothing on the host moves.
/// </summary>
public sealed class RemediationTests
{
    private static readonly Guid AdapterId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void GamingProfile_ProposesOnlyKeywordsTheDriverAdvertisesWithValuesItOffers()
    {
        var capabilities = new[]
        {
            Capability("*InterruptModeration", "1", "0", "1"),
            Capability("*EEE", "1", "0", "1"),
            // Advertised, but this driver only offers "on": the profile must leave it alone.
            Capability("*FlowControl", "3", "3")
        };

        var action = UseCaseProfiles.PlanFor(UseCaseProfiles.Get("competitive-gaming"), AdapterId, capabilities);

        Assert.Equal(
            ["nic.*InterruptModeration", "nic.*EEE"],
            action.Changes.Where(change => change.SettingId.StartsWith("nic.", StringComparison.Ordinal))
                .Select(change => change.SettingId));
        Assert.Contains("*RscIPv4: not advertised", action.TradeOff, StringComparison.Ordinal);
        Assert.Contains("*FlowControl: driver does not offer the value 0", action.TradeOff, StringComparison.Ordinal);
    }

    [Fact]
    public void VendorKeywordsAreNeverProposedByAProfile()
    {
        // A vendor keyword may well be spelled the same; its values are not standardised, so no
        // profile can know what "0" means on it.
        var capabilities = new[] { Capability("EEE", "1", "0", "1") };

        var action = UseCaseProfiles.PlanFor(UseCaseProfiles.Get("calls-and-remote-work"), AdapterId, capabilities);

        Assert.Empty(action.Changes);
    }

    [Fact]
    public void AlreadyMatchingValuesProduceNoChange()
    {
        var capabilities = new[] { Capability("*EEE", "0", "0", "1"), Capability("*FlowControl", "0", "0", "3") };

        var action = UseCaseProfiles.PlanFor(UseCaseProfiles.Get("calls-and-remote-work"), AdapterId, capabilities);

        Assert.Empty(action.Changes);
        Assert.Contains("already matches the profile", action.ExpectedEffect, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamingAndGamingProfilesWeightTheSameKeywordInOppositeDirections()
    {
        var capabilities = new[] { Capability("*RscIPv4", "0", "0", "1") };

        var gaming = UseCaseProfiles.PlanFor(UseCaseProfiles.Get("competitive-gaming"), AdapterId, capabilities);
        var streaming = UseCaseProfiles.PlanFor(UseCaseProfiles.Get("streaming-and-upload"), AdapterId, capabilities);

        Assert.DoesNotContain(gaming.Changes, change => change.SettingId == "nic.*RscIPv4");
        Assert.Equal("1", Assert.Single(streaming.Changes, change => change.SettingId == "nic.*RscIPv4").ProposedValue);
    }

    [Fact]
    public void ProfileChangesAreAlwaysMarkedAsComingFromAProfile()
    {
        var action = UseCaseProfiles.PlanFor(
            UseCaseProfiles.Get("competitive-gaming"), AdapterId, [Capability("*EEE", "1", "0", "1")]);

        Assert.All(action.Changes, change => Assert.Equal(ChangeSource.Profile, change.Source));
        Assert.Equal(RemediationOwner.PresetOrManual, action.Owner);
    }

    [Fact]
    public async Task ProfileChangesSurviveTheTransactionEngineUnchanged()
    {
        // The engine is the only write path, so a profile that proposes something it will refuse is
        // worse than a profile that proposes nothing.
        var capabilities = new[] { Capability("*EEE", "1", "0", "1"), Capability("*InterruptModeration", "1", "0", "1") };
        var action = UseCaseProfiles.PlanFor(UseCaseProfiles.Get("competitive-gaming"), AdapterId, capabilities);
        var transactions = new SettingTransactionService(SettingSpecifications.From(capabilities));
        var store = new MemoryStore();
        foreach (var capability in capabilities)
        {
            store.Values[new NicSettingSpecification(capability).ResolveAddress(AdapterId.ToString())] =
                new StoredSettingValue(true, capability.CurrentValue);
        }

        var plan = await transactions.PrepareAsync(action.Changes, store, CancellationToken.None);

        Assert.Equal(action.Changes.Count, plan.Changes.Count);
        Assert.All(plan.Changes, change => Assert.Equal(ChangeSource.Profile, change.Source));
    }

    [Fact]
    public void OutOfScopeFindingBecomesGuidanceWithNoChangeAtAll()
    {
        var finding = new DiagnosticFinding(
            DiagnosticScope.IspOrRouting, DiagnosticConfidence.High, "Carrier-grade NAT",
            "Hop 2 is 100.72.0.1.", "Ask the ISP for a public address.",
            NetworkSegment.IspAccess, RemediationOwner.OutOfScope);

        var action = Assert.Single(RemediationPlanner.Plan([finding], new RemediationContext()));

        Assert.False(action.AppliesLocally);
        Assert.Equal("No local change", action.ChangesDisplay);
        Assert.Equal("Ask the ISP for a public address.", action.ExpectedEffect);
        Assert.Contains("Keep the runs to compare", action.Verification, StringComparison.Ordinal);
    }

    [Fact]
    public void RouterFindingKeepsItsOwnInstructionRatherThanBeingRewritten()
    {
        var finding = new DiagnosticFinding(
            DiagnosticScope.Lan, DiagnosticConfidence.High, "Congested channel",
            "Six neighbours overlap.", "Set the 2.4 GHz channel to 11 on the router, at 20 MHz width.",
            NetworkSegment.RouterOrAccess, RemediationOwner.Router);

        var action = Assert.Single(RemediationPlanner.Plan([finding], new RemediationContext()));

        Assert.Equal(RemediationOwner.Router, action.Owner);
        Assert.Contains("channel to 11", action.ExpectedEffect, StringComparison.Ordinal);
    }

    [Fact]
    public void BlackHoledPathMtuBecomesAnExactInterfaceChange()
    {
        var finding = LocalFinding();

        var action = Assert.Single(RemediationPlanner.Plan(
            [finding], new RemediationContext(AdapterId, [], BlackHoledPathMtu: 1420)));

        var change = Assert.Single(action.Changes);
        Assert.Equal(("tcp.interface.mtu", "1420"), (change.SettingId, change.ProposedValue));
        SettingCatalog.Get("tcp.interface.mtu").Validate(change.ProposedValue!);
        Assert.Contains("only correct for this path", action.TradeOff, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalFindingWithNoAdapterSelectedStaysGuidance()
    {
        var action = Assert.Single(RemediationPlanner.Plan([LocalFinding()], new RemediationContext()));

        Assert.False(action.AppliesLocally);
    }

    [Fact]
    public void LocalFaultProposesTheAdvertisedEnergyAndPauseKeywordsOnly()
    {
        var capabilities = new[]
        {
            Capability("*EEE", "1", "0", "1"),
            Capability("*FlowControl", "3", "0", "3"),
            Capability("*JumboPacket", "1514", "1514", "9014")
        };

        var action = Assert.Single(RemediationPlanner.Plan(
            [LocalFinding()], new RemediationContext(AdapterId, capabilities)));

        Assert.Equal(["nic.*EEE", "nic.*FlowControl"], action.Changes.Select(change => change.SettingId));
        Assert.Contains("if the counters keep rising, the hardware", action.TradeOff, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tcp.interface.mtu", "575", false)]
    [InlineData("tcp.interface.mtu", "1420", true)]
    [InlineData("tcp.interface.mtu", "9001", false)]
    [InlineData("tcp.interface.netbios-options", "2", true)]
    [InlineData("tcp.interface.netbios-options", "3", false)]
    [InlineData("mmcss.games.gpu-priority", "31", true)]
    [InlineData("mmcss.games.gpu-priority", "32", false)]
    [InlineData("mmcss.games.priority", "8", true)]
    [InlineData("mmcss.games.priority", "0", false)]
    public void NewCatalogEntriesEnforceTheirDocumentedRanges(string id, string value, bool valid)
    {
        var definition = SettingCatalog.Get(id);

        if (valid)
        {
            definition.Validate(value);
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => definition.Validate(value));
        }
    }

    [Fact]
    public void TargetsReportWhatWasNotMeasuredInsteadOfCallingItMet()
    {
        var targets = new RemediationTargets(MinimumThroughputMbps: 50, MaximumPingMs: 30);

        var evaluation = targets.Evaluate(Stats(20));

        Assert.False(evaluation.AllMet);
        var unmet = Assert.Single(evaluation.Unmet);
        Assert.Equal("Throughput", unmet.Metric);
        Assert.Contains("not measured", unmet.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetsAreMetOnlyWhenEveryOneIs()
    {
        var targets = new RemediationTargets(MinimumThroughputMbps: 50, MaximumPingMs: 30, MaximumJitterMs: 5);
        var throughput = new ThroughputResult("http://fake", TransferDirection.Download, 4, 80_000_000, TimeSpan.FromSeconds(10), true);

        Assert.True(targets.Evaluate(Stats(20), throughput).AllMet);
        Assert.False(targets.Evaluate(Stats(45), throughput).AllMet);
    }

    [Fact]
    public void NoTargetsSetIsNotSilentlyAPass()
    {
        Assert.False(new RemediationTargets().Evaluate(Stats(20)).AllMet);
    }

    private static DiagnosticFinding LocalFinding() => new(
        DiagnosticScope.LocalPc, DiagnosticConfidence.High, "The local adapter is discarding frames",
        "500 receive errors.", "Review the adapter settings.",
        NetworkSegment.LocalNicDriver, RemediationOwner.PresetOrManual);

    private static ProbeStatistics Stats(double milliseconds) => ProbeStatistics.Calculate(
        "Game endpoint", "198.51.100.10",
        Enumerable.Range(0, 5).Select(index => new ProbeSample(DateTimeOffset.Now.AddSeconds(index), milliseconds)).ToArray());

    private static AdapterSettingCapability Capability(string keyword, string current, params string[] values)
    {
        var profile = NicKeywordCatalog.For(keyword);
        return new AdapterSettingCapability(
            AdapterId, "Ethernet", "Test adapter", keyword, keyword, current, null,
            values.Select(value => new CapabilityChoice(value, value)).ToArray(),
            null, null, null, AdapterSettingCapability.RegistrySz, false,
            profile.Areas, profile.Risk, profile.TradeOff);
    }

    private sealed class MemoryStore : ISettingStore
    {
        public Dictionary<SettingAddress, StoredSettingValue> Values { get; } = [];

        public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken) =>
            Task.FromResult(Values.GetValueOrDefault(address, StoredSettingValue.Missing));

        public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken)
        {
            if (value.Exists)
            {
                Values[address] = value;
            }
            else
            {
                Values.Remove(address);
            }

            return Task.CompletedTask;
        }
    }
}
