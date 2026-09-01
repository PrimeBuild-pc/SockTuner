using System.Text;
using SockTuner.Models;
using SockTuner.Services.Remediation;

namespace SockTuner.Persistence;

/// <summary>
/// Turns the recommendations into a checklist that leaves the app.
/// </summary>
/// <remarks>
/// <para>
/// Router work is the case this exists for. Those instructions name a parameter, a value and the
/// reason for that value, and the person acting on them is standing in a different web interface —
/// or in an SSH session — while they do it. A block of text inside a WPF window is the one place
/// that is no use for that. Markdown because it is the format that stays readable as plain text,
/// pastes into an issue or a notes app unchanged, and diffs when the advice is regenerated after a
/// second measurement.
/// </para>
/// <para>
/// Every item keeps the verification step next to it. A checklist of changes with no way to tell
/// whether they worked is how tuning advice becomes folklore, and this app's whole argument is that
/// a change is worth exactly what the re-measurement says it is worth.
/// </para>
/// </remarks>
public static class ActionChecklistExporter
{
    public static string ToMarkdown(
        IReadOnlyList<RemediationAction> actions,
        IReadOnlyList<RouterGuidanceItem> router,
        string? measurementSummary = null,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(router);

        var builder = new StringBuilder();
        builder.AppendLine("# SockTuner action checklist");
        builder.AppendLine();
        builder.AppendLine($"Generated {(generatedAt ?? DateTimeOffset.Now):yyyy-MM-dd HH:mm zzz}.");
        builder.AppendLine();
        builder.AppendLine(
            "Every item below was derived from a measurement, not from a preset. Nothing here has been "
            + "applied; the local changes still have to be previewed and confirmed in SockTuner's tuning "
            + "plan, and the router changes are made on the router.");

        if (measurementSummary is { Length: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine("## What this is based on");
            builder.AppendLine();
            builder.AppendLine(measurementSummary);
        }

        var local = actions.Where(action => action.AppliesLocally).ToArray();
        builder.AppendLine();
        builder.AppendLine("## On this machine");
        builder.AppendLine();
        if (local.Length == 0)
        {
            builder.AppendLine("_Nothing here is a change this machine can make._");
        }

        foreach (var action in local)
        {
            builder.AppendLine($"- [ ] **{action.Title}**");
            foreach (var change in action.Changes)
            {
                builder.AppendLine($"  - `{change.SettingId}` = `{change.ProposedValue ?? "(remove the value)"}`");
            }

            AppendIfPresent(builder, "Expected effect", action.ExpectedEffect);
            AppendIfPresent(builder, "Trade-off", action.TradeOff);
            AppendIfPresent(builder, "How to verify", action.Verification);
            builder.AppendLine();
        }

        var guidance = actions.Where(action => !action.AppliesLocally).ToArray();
        if (guidance.Length > 0)
        {
            builder.AppendLine("## Belongs elsewhere");
            builder.AppendLine();
            foreach (var action in guidance)
            {
                builder.AppendLine($"- [ ] **{action.Title}** — {action.Owner}");
                AppendIfPresent(builder, "Expected effect", action.ExpectedEffect);
                AppendIfPresent(builder, "How to verify", action.Verification);
                builder.AppendLine();
            }
        }

        builder.AppendLine("## On the router");
        builder.AppendLine();
        if (router.Count == 0)
        {
            builder.AppendLine("_Nothing measured here needs a router change._");
            builder.AppendLine();
        }

        foreach (var item in router)
        {
            builder.AppendLine($"### {item.Title}");
            builder.AppendLine();
            builder.AppendLine("| Setting | Value | Why |");
            builder.AppendLine("| --- | --- | --- |");
            foreach (var instruction in item.Instructions)
            {
                builder.AppendLine(
                    $"| {Cell(instruction.Parameter)} | `{Cell(instruction.Value)}` | {Cell(instruction.Reason)} |");
            }

            builder.AppendLine();

            // The UCI lines are the reason this export beats a screenshot: on OpenWrt they are
            // typed or pasted as they stand.
            var uci = item.Instructions.Where(instruction => instruction.UciPath is not null).ToArray();
            if (uci.Length > 0)
            {
                builder.AppendLine("OpenWrt, as commands:");
                builder.AppendLine();
                builder.AppendLine("```sh");
                foreach (var instruction in uci)
                {
                    builder.AppendLine($"uci set {instruction.UciPath}='{Shell(instruction.Value)}'");
                }

                builder.AppendLine("uci commit");
                builder.AppendLine("```");
                builder.AppendLine();
            }

            builder.AppendLine($"- [ ] Verify: {item.Verification}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void AppendIfPresent(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.AppendLine($"  - _{label}:_ {value}");
        }
    }

    /// <summary>A pipe inside a cell would end the column early, so it is escaped.</summary>
    private static string Cell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");

    /// <summary>
    /// The value is quoted in single quotes in the generated command, so an embedded single quote
    /// has to be closed and reopened. These values come from this app's own catalogue rather than
    /// from user input, but a generated shell line is not the place to rely on that.
    /// </summary>
    private static string Shell(string value) => value.Replace("'", "'\\''", StringComparison.Ordinal);
}
