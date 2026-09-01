using SockTuner.Models;
using SockTuner.Persistence;
using SockTuner.Services.Diagnosis;
using SockTuner.Services.Remediation;

namespace SockTuner.Tests;

public sealed class ActionChecklistExporterTests
{
    private static RemediationAction Local() => new(
        "remediation.0.nic",
        "Turn interrupt moderation off",
        NetworkSegment.LocalNicDriver,
        RemediationOwner.PresetOrManual,
        [new ChangeRequest("nic.*InterruptModeration", "adapter", "0")],
        "Each packet is delivered as it lands instead of in batches.",
        "Costs CPU on a busy link.",
        "Re-run the loaded-latency measurement.");

    private static RemediationAction Guidance() => new(
        "remediation.1.isp",
        "Ask the provider about the upstream queue",
        NetworkSegment.IspAccess,
        RemediationOwner.OutOfScope,
        [],
        "Nothing local can drain a queue that is not here.",
        string.Empty,
        "Re-measure after they act.");

    private static RouterGuidanceItem Router() => new(
        "Shape the link on the router",
        NetworkSegment.RouterOrAccess,
        [
            new RouterInstruction("Queue discipline", "cake", "Keeps the queue short.", "sqm.@queue[0].qdisc"),
            new RouterInstruction("SQM download limit", "90000 kbit/s", "90% of the measured rate.", "sqm.@queue[0].download"),
            new RouterInstruction("Link layer adaptation", "depends on the access technology", "Declare the overhead.")
        ],
        "Re-run the loaded-latency measurement; the grade should move to A or B.");

    private static string Export() =>
        ActionChecklistExporter.ToMarkdown([Local(), Guidance()], [Router()], "Download: idle 10 ms, loaded 60 ms.");

    [Fact]
    public void LocalChangesAppearAsTickableItemsWithTheirSettingAndValue()
    {
        var markdown = Export();

        Assert.Contains("- [ ] **Turn interrupt moderation off**", markdown, StringComparison.Ordinal);
        Assert.Contains("`nic.*InterruptModeration` = `0`", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryItemCarriesHowToVerifyIt()
    {
        var markdown = Export();

        // A checklist of changes with no way to tell whether they worked is how tuning advice
        // turns into folklore.
        Assert.Contains("Re-run the loaded-latency measurement.", markdown, StringComparison.Ordinal);
        Assert.Contains("the grade should move to A or B", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatSomebodyElseOwnsIsListedSeparatelyAndNotAsALocalChange()
    {
        var markdown = Export();
        var elsewhere = markdown.IndexOf("## Belongs elsewhere", StringComparison.Ordinal);
        var machine = markdown.IndexOf("## On this machine", StringComparison.Ordinal);

        Assert.True(machine >= 0 && elsewhere > machine);
        Assert.Contains("Ask the provider about the upstream queue", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RouterInstructionsBecomeATableOfSettingValueAndReason()
    {
        var markdown = Export();

        Assert.Contains("| Setting | Value | Why |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Queue discipline | `cake` | Keeps the queue short. |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenWrtPathsBecomeCommandsThatCanBePastedAsTheyAre()
    {
        var markdown = Export();

        Assert.Contains("uci set sqm.@queue[0].qdisc='cake'", markdown, StringComparison.Ordinal);
        Assert.Contains("uci set sqm.@queue[0].download='90000 kbit/s'", markdown, StringComparison.Ordinal);
        Assert.Contains("uci commit", markdown, StringComparison.Ordinal);

        // The third instruction has no UCI path, so it must not become a command with an empty one.
        Assert.DoesNotContain("uci set ='", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueContainingAQuoteCannotBreakOutOfTheGeneratedCommand()
    {
        var markdown = ActionChecklistExporter.ToMarkdown(
            [],
            [new RouterGuidanceItem("Odd", NetworkSegment.RouterOrAccess,
                [new RouterInstruction("Name", "it's", "Because.", "sqm.@queue[0].name")],
                "Check it.")]);

        Assert.Contains(@"uci set sqm.@queue[0].name='it'\''s'", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void APipeInAReasonDoesNotEndTheTableColumnEarly()
    {
        var markdown = ActionChecklistExporter.ToMarkdown(
            [],
            [new RouterGuidanceItem("Odd", NetworkSegment.RouterOrAccess,
                [new RouterInstruction("Name", "value", "a | b", null)],
                "Check it.")]);

        Assert.Contains(@"| a \| b |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBasisForTheAdviceIsRecordedSoTheChecklistIsNotAdviceFromNowhere()
    {
        var markdown = Export();

        Assert.Contains("## What this is based on", markdown, StringComparison.Ordinal);
        Assert.Contains("idle 10 ms, loaded 60 ms", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySetSaysSoRatherThanProducingAnEmptyDocument()
    {
        var markdown = ActionChecklistExporter.ToMarkdown([], []);

        Assert.Contains("Nothing here is a change this machine can make", markdown, StringComparison.Ordinal);
        Assert.Contains("Nothing measured here needs a router change", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChecklistStatesThatNothingHasBeenAppliedYet()
    {
        // The file leaves the app and is read later, possibly by someone else. It must not read as
        // a record of work already done.
        Assert.Contains("Nothing here has been", Export(), StringComparison.Ordinal);
    }
}
