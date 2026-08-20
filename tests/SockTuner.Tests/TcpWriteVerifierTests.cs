using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

/// <summary>
/// The verifier runs against fakes here. Its whole purpose is to be run for real inside a
/// disposable VM, so what is checked here is that it reports refusals and rollback failures
/// honestly rather than swallowing them.
/// </summary>
public sealed class TcpWriteVerifierTests
{
    [Fact]
    public async Task AWritableTemplateThatCarriesTrafficIsTheGoodOutcome()
    {
        var report = await Run(new FakeStore(), "Internet", "Internet", "InternetCustom");

        Assert.All(report.Outcomes, outcome => Assert.True(outcome.Accepted));
        Assert.All(report.Outcomes, outcome => Assert.True(outcome.Restored));
        Assert.Contains("accepts writes and rolled back exactly", report.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATemplateThatRefusesIsRecordedRatherThanThrown()
    {
        var store = new FakeStore { RefuseTemplate = "Internet" };

        var report = await Run(store, "Internet", "Internet", "InternetCustom");

        var refused = Assert.Single(report.Outcomes, outcome => !outcome.Accepted);
        Assert.Equal("Internet", refused.Template);
        Assert.NotNull(refused.Error);
    }

    [Fact]
    public async Task TheDangerousCombinationIsCalledOutInPlainWords()
    {
        // The built-in template carries the traffic and refuses the write; the Custom one accepts it
        // and carries nothing. That is the case where offering these settings would be a lie.
        var store = new FakeStore { RefuseTemplate = "Internet" };

        var report = await Run(store, "Internet", "Internet", "InternetCustom");

        Assert.Contains("REFUSED", report.Verdict, StringComparison.Ordinal);
        Assert.Contains("transport filter at a writable template", report.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoTemplateAcceptingAnythingIsSaidPlainly()
    {
        var store = new FakeStore { RefuseTemplate = "*" };

        var report = await Run(store, "Internet", "Internet", "InternetCustom");

        Assert.Contains("No template accepted a write", report.Verdict, StringComparison.Ordinal);
        Assert.Contains("catalog should say so", report.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRollbackOutranksEveryOtherConclusion()
    {
        var store = new FakeStore { FailRestoreTemplate = "InternetCustom" };

        var report = await Run(store, "Internet", "Internet", "InternetCustom");

        Assert.StartsWith("ROLLBACK FAILED on InternetCustom", report.Verdict, StringComparison.Ordinal);
        Assert.Contains("left changed", report.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCanaryIsRestoredToTheExactValueItStartedFrom()
    {
        var store = new FakeStore();

        await Run(store, "Internet", "Internet");

        Assert.All(store.Values, pair => Assert.Equal("0", pair.Value.Value));
    }

    [Fact]
    public async Task WarningsRaisedDuringAWriteAreAttachedToThatTemplate()
    {
        var store = new FakeStore();
        var warnings = new List<string>();
        var capabilities = new[] { Canary("Internet"), Canary("InternetCustom") };
        Seed(store, capabilities);
        store.OnWrite = address => warnings.Add($"warning for {address.TargetId}");

        var report = await new TcpWriteVerifier(new SettingTransactionService(SettingSpecifications.From([], capabilities)))
            .RunAsync(capabilities, Resolution("Internet"), store, () => warnings, CancellationToken.None);

        Assert.All(report.Outcomes, outcome => Assert.NotEmpty(outcome.Warnings));
        Assert.Contains(report.Outcomes.SelectMany(outcome => outcome.Warnings), item => item.Contains("InternetCustom", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCanaryIsTheLowestRiskEntryInTheCatalog()
    {
        var canary = CimGlobalPropertyCatalog.Find(
            CimGlobalPropertyCatalog.TcpSettingClass, TcpWriteVerifier.CanaryProperty);

        Assert.NotNull(canary);
        Assert.Equal(ChangeRisk.Low, canary.Risk);
        Assert.DoesNotContain(CimGlobalPropertyCatalog.All.Where(item => item.ClassName == CimGlobalPropertyCatalog.TcpSettingClass),
            item => item.Risk < ChangeRisk.Low);
    }

    private static async Task<TcpWriteVerificationReport> Run(FakeStore store, string resolved, params string[] templates)
    {
        var capabilities = templates.Select(Canary).ToArray();
        Seed(store, capabilities);
        var verifier = new TcpWriteVerifier(new SettingTransactionService(SettingSpecifications.From([], capabilities)));
        return await verifier.RunAsync(capabilities, Resolution(resolved), store, () => [], CancellationToken.None);
    }

    private static void Seed(FakeStore store, IReadOnlyList<GlobalSettingCapability> capabilities)
    {
        foreach (var capability in capabilities)
        {
            store.Values[new CimGlobalSettingSpecification(capability).ResolveAddress(capability.InstanceKey)] =
                new StoredSettingValue(true, capability.CurrentValue);
        }
    }

    private static TcpTemplateResolution Resolution(string template) =>
        new(template, true, [], $"filter points at {template}");

    private static GlobalSettingCapability Canary(string template) => new(
        CimGlobalPropertyCatalog.TcpSettingClass, template, TcpWriteVerifier.CanaryProperty,
        "Non-SACK RTT resiliency", "TCP options", "0",
        [new CapabilityChoice("0", "Disabled"), new CapabilityChoice("1", "Enabled")],
        null, null, EvidenceLevel.Documented, ChangeRisk.Low, "None", "Test trade-off.");

    private sealed class FakeStore : ISettingStore
    {
        public Dictionary<SettingAddress, StoredSettingValue> Values { get; } = [];
        public string? RefuseTemplate { get; init; }
        public string? FailRestoreTemplate { get; init; }
        public Action<SettingAddress>? OnWrite { get; set; }

        private readonly HashSet<SettingAddress> _written = [];

        public Task<StoredSettingValue> ReadAsync(SettingAddress address, CancellationToken cancellationToken) =>
            Task.FromResult(Values.GetValueOrDefault(address, StoredSettingValue.Missing));

        public Task WriteAsync(SettingAddress address, StoredSettingValue value, CancellationToken cancellationToken)
        {
            OnWrite?.Invoke(address);
            if (RefuseTemplate is "*" || RefuseTemplate == address.TargetId)
            {
                throw new InvalidOperationException($"The provider refused a write to {address.TargetId}.");
            }

            // Refusing only the restore is how a half-applied change is simulated.
            if (FailRestoreTemplate == address.TargetId && !_written.Add(address))
            {
                throw new InvalidOperationException($"The provider refused the restore on {address.TargetId}.");
            }

            Values[address] = value;
            return Task.CompletedTask;
        }
    }
}
