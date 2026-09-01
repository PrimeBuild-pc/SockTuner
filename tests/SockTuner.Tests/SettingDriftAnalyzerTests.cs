using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services.Diagnosis;

namespace SockTuner.Tests;

/// <summary>
/// Drift is a pure comparison between the audit and a read-back, so it is tested entirely against
/// a fake reader. Nothing here touches a driver.
/// </summary>
public sealed class SettingDriftAnalyzerTests
{
    private const string Adapter = "{11111111-2222-3333-4444-555555555555}";

    private static TransactionAuditEntry Entry(
        TransactionAuditOutcome outcome,
        DateTimeOffset at,
        params AuditedSettingChange[] changes) =>
        new(2, Guid.NewGuid(), at, outcome, Guid.NewGuid(), changes, null);

    private static AuditedSettingChange Change(string settingId, string before, string after) =>
        new(settingId, Adapter, new AuditStoredValue(true, before), new AuditStoredValue(true, after),
            ChangeSource.Manual);

    private static SettingDriftAnalyzer.Read Reads(params (string SettingId, string Value)[] values) =>
        (settingId, _) => values.FirstOrDefault(item => item.SettingId == settingId) is { SettingId: not null } match
            ? new StoredSettingValue(true, match.Value)
            : throw new KeyNotFoundException($"{settingId} is not advertised any more.");

    private static readonly DateTimeOffset Monday = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASettingStillHoldingWhatWasWrittenIsNotDrift()
    {
        var drift = SettingDriftAnalyzer.Compare(
            [Entry(TransactionAuditOutcome.ApplySucceeded, Monday, Change("nic.*EEE", "1", "0"))],
            Reads(("nic.*EEE", "0")));

        Assert.Equal(DriftState.Holding, Assert.Single(drift).State);
    }

    [Fact]
    public void ASettingPutBackToItsDefaultElsewhereIsReportedAsDrift()
    {
        // The case this exists for: a driver update reinstalls the INF and restores every default.
        var drift = Assert.Single(SettingDriftAnalyzer.Compare(
            [Entry(TransactionAuditOutcome.ApplySucceeded, Monday, Change("nic.*EEE", "1", "0"))],
            Reads(("nic.*EEE", "1"))));

        Assert.Equal(DriftState.Drifted, drift.State);
        Assert.Equal("0", drift.ExpectedDisplay);
        Assert.Equal("1", drift.ActualDisplay);
        Assert.Equal(Monday, drift.WrittenAt);
    }

    [Fact]
    public void ARollbackReplacesTheApplyAsTheExpectation()
    {
        // SockTuner put the value back on purpose. The restored value is what should be there now;
        // treating the earlier apply as the expectation would report the app's own rollback as drift.
        var drift = Assert.Single(SettingDriftAnalyzer.Compare(
            [
                Entry(TransactionAuditOutcome.ApplySucceeded, Monday, Change("nic.*EEE", "1", "0")),
                Entry(TransactionAuditOutcome.RollbackSucceeded, Monday.AddHours(1), Change("nic.*EEE", "0", "1"))
            ],
            Reads(("nic.*EEE", "1"))));

        Assert.Equal(DriftState.Holding, drift.State);
    }

    [Fact]
    public void AFailedTransactionEstablishesNoExpectation()
    {
        var drift = SettingDriftAnalyzer.Compare(
            [Entry(TransactionAuditOutcome.ApplyFailed, Monday, Change("nic.*EEE", "1", "0"))],
            Reads(("nic.*EEE", "1")));

        Assert.Empty(drift);
    }

    [Fact]
    public void TheMostRecentSuccessfulWriteWins()
    {
        var drift = Assert.Single(SettingDriftAnalyzer.Compare(
            [
                Entry(TransactionAuditOutcome.ApplySucceeded, Monday, Change("nic.ITR", "64", "0")),
                Entry(TransactionAuditOutcome.ApplySucceeded, Monday.AddDays(1), Change("nic.ITR", "0", "125"))
            ],
            Reads(("nic.ITR", "125"))));

        Assert.Equal(DriftState.Holding, drift.State);
        Assert.Equal("125", drift.ExpectedDisplay);
    }

    [Fact]
    public void AKeywordTheDriverNoLongerAdvertisesIsReportedRatherThanTreatedAsUnchanged()
    {
        var drift = Assert.Single(SettingDriftAnalyzer.Compare(
            [Entry(TransactionAuditOutcome.ApplySucceeded, Monday, Change("nic.*Gone", "1", "0"))],
            Reads(("nic.*EEE", "0"))));

        Assert.Equal(DriftState.Unreadable, drift.State);
        Assert.NotNull(drift.Error);
    }

    [Fact]
    public void AnAbsentValueNeverMatchesAPresentOne()
    {
        var removed = new AuditedSettingChange(
            "irq.affinity", Adapter,
            new AuditStoredValue(true, "4:0:0x3"), new AuditStoredValue(false, string.Empty),
            ChangeSource.Manual);

        var drift = Assert.Single(SettingDriftAnalyzer.Compare(
            [Entry(TransactionAuditOutcome.ApplySucceeded, Monday, removed)],
            (_, _) => new StoredSettingValue(true, "4:0:0x3")));

        Assert.Equal(DriftState.Drifted, drift.State);
        Assert.Equal("Removed", drift.ExpectedDisplay);
    }

    [Fact]
    public void ValuesCompareOrdinallyBecauseAnNdisKeywordIsText()
    {
        var drift = Assert.Single(SettingDriftAnalyzer.Compare(
            [Entry(TransactionAuditOutcome.ApplySucceeded, Monday, Change("nic.*JumboPacket", "1514", "1"))],
            Reads(("nic.*JumboPacket", "01"))));

        Assert.Equal(DriftState.Drifted, drift.State);
    }

    [Fact]
    public void DriftedSettingsSortAboveTheOnesStillHolding()
    {
        var drift = SettingDriftAnalyzer.Compare(
            [Entry(TransactionAuditOutcome.ApplySucceeded, Monday,
                Change("nic.*AAA", "1", "0"), Change("nic.*ZZZ", "1", "0"))],
            Reads(("nic.*AAA", "0"), ("nic.*ZZZ", "1")));

        Assert.Equal("nic.*ZZZ", drift[0].SettingId);
        Assert.Equal(DriftState.Drifted, drift[0].State);
    }

    [Fact]
    public void TheSummaryDistinguishesNothingWrittenFromNothingDrifted()
    {
        Assert.Contains("has not applied anything", SettingDriftAnalyzer.Summarise([]), StringComparison.Ordinal);

        var holding = SettingDriftAnalyzer.Compare(
            [Entry(TransactionAuditOutcome.ApplySucceeded, Monday, Change("nic.*EEE", "1", "0"))],
            Reads(("nic.*EEE", "0")));
        Assert.Contains("still hold the value", SettingDriftAnalyzer.Summarise(holding), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryStateCarriesItsGlyphSoTheColumnReadsWithoutColour()
    {
        var drift = SettingDriftAnalyzer.Compare(
            [Entry(TransactionAuditOutcome.ApplySucceeded, Monday, Change("nic.*EEE", "1", "0"))],
            Reads(("nic.*EEE", "1")));

        Assert.StartsWith(Badges.Middling, Assert.Single(drift).StateBadge, StringComparison.Ordinal);
    }
}
