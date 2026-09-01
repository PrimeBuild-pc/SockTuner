using System.IO;
using SockTuner.Models;
using SockTuner.Services.Diagnosis;
using SockTuner.Services.Remediation;

namespace SockTuner.Tests;

/// <summary>
/// The two external bufferbloat formats. The deterministic tests run against committed fixtures
/// built to exercise the awkward parts — warm-up phases far worse than the measured ones, a lost
/// probe, one direction much cleaner than the other. Parsing a file touches nothing but the file.
/// </summary>
public sealed class BufferbloatReportImporterTests
{
    private const string WaveformFixture = "tests/SockTuner.Tests/Fixtures/waveform-export.csv";
    private const string JsonFixture = "tests/SockTuner.Tests/Fixtures/bufferbloat-report.json";

    private const string RealWaveform =
        "research/results/waveform_com_bufferbloat_test_results_9d9f65e0-13cf-4e34-bc99-88a97225cd2f.csv";
    private const string RealJson = "research/results/bufferbloat-test-report-2026-08-28T12-12-12.json";

    private static ImportedBufferbloatReport Waveform() =>
        BufferbloatReportImporter.Load(TestPaths.InRepository(WaveformFixture));

    private static ImportedBufferbloatReport Json() =>
        BufferbloatReportImporter.Load(TestPaths.InRepository(JsonFixture));

    // ---- Waveform CSV ----------------------------------------------------------------------

    [Fact]
    public void TheWaveformExportIsRecognisedAndIdentified()
    {
        var report = Waveform();

        Assert.Equal(BufferbloatReportSource.Waveform, report.Source);
        Assert.Equal("00000000-0000-4000-8000-000000000001", report.TestId);
        Assert.Equal("B", report.ReportedGrade);
        Assert.Equal(2025, report.CapturedAt.Year);
    }

    [Fact]
    public void BothWaveformDirectionsCarryTheirOwnMeasuredRate()
    {
        var report = Waveform();

        Assert.Equal(100.5, report.Download!.Load.BitsPerSecond / 1_000_000d, 3);
        Assert.Equal(20.25, report.Upload!.Load.BitsPerSecond / 1_000_000d, 3);
    }

    [Fact]
    public void EachDirectionIsGradedSeparatelyAndTheWorstOneDecides()
    {
        var report = Waveform();

        // Idle 10 ms. Download settles at 12 (+2, clean); upload at 45 (+35, a real queue). Grading
        // them together would average the bad direction away, which is the flattery this avoids.
        Assert.Equal(2.0, report.Download!.LatencyIncreaseMs!.Value, 1);
        Assert.Equal(35.0, report.Upload!.LatencyIncreaseMs!.Value, 1);
        Assert.Equal(BufferbloatGrade.B, report.DerivedGrade);
    }

    [Fact]
    public void TheDerivedGradeIsComputedHereRatherThanCopiedFromTheFile()
    {
        var report = Waveform();

        // Same letter, arrived at independently: the file says B in its summary, and the app
        // reaches B from the file's own samples. Both are shown so a disagreement is visible.
        Assert.Equal("B", report.ReportedGrade);
        Assert.Equal(BufferbloatGrade.B, report.DerivedGrade);
    }

    [Fact]
    public void TheIdleBaselineIsSharedByBothDirections()
    {
        var report = Waveform();

        // One unloaded measurement in the file, so both directions compare against it.
        Assert.Equal(report.Download!.Idle.MedianMs, report.Upload!.Idle.MedianMs);
        Assert.Equal(40, report.Download.Idle.Sent);
        Assert.Equal(60, report.Download.Loaded.Sent);
        Assert.Equal(60, report.Upload.Loaded.Sent);
    }

    // ---- sampled JSON ----------------------------------------------------------------------

    [Fact]
    public void TheJsonReportIsRecognisedAndIdentified()
    {
        var report = Json();

        Assert.Equal(BufferbloatReportSource.SampledJson, report.Source);
        Assert.Equal("00000000-0000-4000-8000-000000000002", report.TestId);
        Assert.Equal("C", report.ReportedGrade);
        Assert.Equal("Example ISP", report.Provider);
    }

    [Fact]
    public void TheWarmUpPhasesAreExcludedFromTheGrade()
    {
        var report = Json();

        // The fixture's warm-up phases sit at 500 and 600 ms against a 20 ms baseline. Counting
        // them would drag both directions to F; the queue is still filling there, so they are not
        // a measurement of anything.
        Assert.Equal(35, report.Download!.Loaded.Sent);
        Assert.Equal(28, report.Upload!.Loaded.Sent);
        Assert.Equal(30, report.Download.Idle.Sent);
        Assert.Equal(BufferbloatGrade.C, LoadedLatencyAnalyzer.Grade(report.Download.LatencyIncreaseMs!.Value));
        Assert.Equal(BufferbloatGrade.A, LoadedLatencyAnalyzer.Grade(report.Upload.LatencyIncreaseMs!.Value));
    }

    [Fact]
    public void ALostProbeCountsAsLossRatherThanAsARoundTrip()
    {
        var report = Json();

        // The fixture holds 35 answered download probes plus two flagged lost. A lost probe has no
        // round trip to contribute, so it must not become a 0 ms sample that flatters the median.
        Assert.Equal(35, report.Download!.Loaded.Sent);
        Assert.True(report.Download.Loaded.MedianMs > 80);
    }

    [Fact]
    public void TheBidirectionalPhaseIsReportedAsANoteRatherThanSilentlyGraded()
    {
        var report = Json();

        Assert.Contains(report.Notes, note => note.Contains("both directions at once", StringComparison.Ordinal));
    }

    [Fact]
    public void TheJsonThroughputIsCarriedThroughForBothDirections()
    {
        var report = Json();

        Assert.Equal(250.5, report.Download!.Load.BitsPerSecond / 1_000_000d, 2);
        Assert.Equal(40.125, report.Upload!.Load.BitsPerSecond / 1_000_000d, 2);
    }

    // ---- what the import is for -------------------------------------------------------------

    /// <summary>
    /// The whole point of the import, end to end: a file, and no gaming diagnosis at all, still
    /// produces the kinds of change the recommendations surface offers — NIC keywords the driver
    /// advertises, the system settings the same profile carries, and router instructions. The
    /// surface used to refuse to build without a diagnosis; this is the case that must keep working.
    /// </summary>
    [Fact]
    public void AnImportAloneProducesLocalChangesAndRouterInstructions()
    {
        var report = Waveform();
        var adapterId = Guid.NewGuid();
        IReadOnlyList<AdapterSettingCapability> capabilities =
        [
            Keyword(adapterId, "*InterruptModeration"),
            Keyword(adapterId, "*FlowControl")
        ];

        var profile = UseCaseProfiles.All.Single(item => item.Id == "competitive-gaming");
        var local = UseCaseProfiles.PlanFor(profile, adapterId, capabilities);

        Assert.True(local.AppliesLocally);
        Assert.Contains(local.Changes, change => change.SettingId.StartsWith("nic.", StringComparison.Ordinal));
        Assert.All(local.Changes, change => Assert.True(
            change.SettingId.StartsWith("nic.", StringComparison.Ordinal)
            || change.SettingId.StartsWith("mmcss.", StringComparison.Ordinal),
            $"unexpected setting family: {change.SettingId}"));

        // The planner runs with no findings at all, which is exactly what an import gives it.
        Assert.Empty(RemediationPlanner.Plan([], new RemediationContext(adapterId, capabilities)));

        var guidance = RouterGuidance.For(new RouterGuidanceInput(report.Download, report.Upload));
        Assert.All(guidance, item => Assert.NotEmpty(item.Instructions));
    }

    [Fact]
    public void ABloatedImportProducesShapingInstructionsBelowTheMeasuredRate()
    {
        var baseline = string.Join('\n', Enumerable.Repeat("20", 40));
        var loaded = string.Join('\n', Enumerable.Repeat("400", 40));
        var report = BufferbloatReportImporter.Parse($"""
            ====== WAVEFORM.COM BUFFERBLOAT TEST RESULTS======
            Test ID,bloated
            Bufferbloat Grade,F
            Download speed (Mbps),100
            Upload speed (Mbps),20
            ====== UNLOADED LATENCY MEASUREMENTS (ms) ======
            {baseline}
            ====== DOWNLOAD STAGE LATENCY MEASUREMENTS (ms) ======
            {loaded}
            """);

        // 20 ms idle against 400 ms loaded is a 380 ms increase: grade D, since F starts at 400.
        Assert.Equal(BufferbloatGrade.D, report.DerivedGrade);

        var shaping = RouterGuidance.For(new RouterGuidanceInput(report.Download, report.Upload))
            .SelectMany(item => item.Instructions)
            .ToArray();

        Assert.NotEmpty(shaping);

        // 90 Mbit/s is 90% of the 100 the file recorded: the shaped rate has to sit under a rate
        // that was actually reached, or the queue stays upstream where the router cannot manage it.
        Assert.Contains(shaping, instruction => instruction.Value.Contains("90", StringComparison.Ordinal));
    }

    // ---- refusals ----------------------------------------------------------------------------

    [Fact]
    public void AFileThatIsNeitherFormatIsRefused() =>
        Assert.Throws<InvalidDataException>(() => BufferbloatReportImporter.Parse("latency,9.4\nspeed,100"));

    [Fact]
    public void JsonWithoutLatencySamplesIsRefused() =>
        Assert.Throws<InvalidDataException>(() =>
            BufferbloatReportImporter.Parse("""{"schemaVersion":1,"summary":{"totalGrade":"A"}}"""));

    [Fact]
    public void JsonWithNoBaselinePhaseIsRefused() =>
        Assert.Throws<InvalidDataException>(() => BufferbloatReportImporter.Parse(
            """{"latencySamples":[{"phase":"download","rttMs":40.0}]}"""));

    [Fact]
    public void AWaveformExportWithNoUnloadedSectionIsRefused() =>
        Assert.Throws<InvalidDataException>(() => BufferbloatReportImporter.Parse(
            "====== WAVEFORM.COM BUFFERBLOAT TEST RESULTS====== \nTest ID,x\n"));

    [Fact]
    public void AnEmptyReportIsRefused() =>
        Assert.Throws<ArgumentException>(() => BufferbloatReportImporter.Parse("   "));

    [Fact]
    public void ADirectionMissingFromTheFileIsReportedRatherThanInvented()
    {
        var report = BufferbloatReportImporter.Parse("""
            {"latencySamples":[
              {"phase":"baseline","rttMs":10.0},{"phase":"baseline","rttMs":11.0},
              {"phase":"download","rttMs":40.0},{"phase":"download","rttMs":42.0}]}
            """);

        Assert.NotNull(report.Download);
        Assert.Null(report.Upload);
        Assert.Contains(report.Notes, note => note.Contains("No upload phase", StringComparison.Ordinal));
    }

    // ---- the private corpus ------------------------------------------------------------------

    /// <summary>
    /// The real exports this importer was written against. They live outside the repository on
    /// purpose, so these skip when absent; what they check is that the committed fixtures have not
    /// drifted away from the shape of a genuine file.
    /// </summary>
    [LocalCorpusFact(RealWaveform)]
    public void TheRealWaveformExportStillParses()
    {
        var report = BufferbloatReportImporter.Load(TestPaths.InRepository(RealWaveform));

        Assert.Equal(BufferbloatReportSource.Waveform, report.Source);
        Assert.NotNull(report.Download);
        Assert.NotNull(report.Upload);
        Assert.NotNull(report.DerivedGrade);
    }

    [LocalCorpusFact(RealJson)]
    public void TheRealJsonReportStillParses()
    {
        var report = BufferbloatReportImporter.Load(TestPaths.InRepository(RealJson));

        Assert.Equal(BufferbloatReportSource.SampledJson, report.Source);
        Assert.NotNull(report.Download);
        Assert.NotNull(report.Upload);
        Assert.NotNull(report.DerivedGrade);
    }

    private static AdapterSettingCapability Keyword(Guid adapterId, string keyword) =>
        new(adapterId, "Ethernet", "Test adapter", keyword, keyword.TrimStart('*'),
            CurrentValue: "1", DefaultValue: "1",
            Choices: [new CapabilityChoice("0", "Disabled"), new CapabilityChoice("1", "Enabled")],
            Minimum: null, Maximum: null, Step: null,
            RegistryDataType: AdapterSettingCapability.RegistrySz,
            CanRemove: false, Areas: TuningArea.Latency, Risk: ChangeRisk.Medium,
            TradeOff: "Test fixture.");
}
