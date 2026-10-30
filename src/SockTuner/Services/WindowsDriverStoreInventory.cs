using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// Reads every driver package in the Windows driver store via <c>pnputil /enum-drivers</c> — a
/// read, no elevation needed. Parsed positionally (first match of each labelled field, in the fixed
/// order pnputil documents: Published Name, Original Name, Provider Name, Class Name, Class GUID,
/// Driver Version), not by matching the English label text, because pnputil's own labels are
/// localised on a non-English Windows install while the field order is the stable, documented part.
/// </summary>
internal static class WindowsDriverStoreInventory
{
    // The label itself is intentionally unconstrained (not [A-Za-z ]+): on a non-English Windows
    // install pnputil prints a localized label (e.g. "Опубликованное имя:"), and only the fixed
    // field *order* this class relies on is actually locale-stable, never the label text.
    private static readonly Regex FieldLine = new(@"^(.+?):\s+(.*)$", RegexOptions.Compiled);

    internal static DriverStoreInventoryResult Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], null);
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("pnputil.exe", "/enum-drivers")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("pnputil could not be started.");
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0
                ? new(ParseRecords(output), null)
                : new([], $"pnputil /enum-drivers exited with code {process.ExitCode}.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new([], $"Could not read the driver store: {exception.Message}");
        }
    }

    internal static IReadOnlyList<DriverStorePackage> ParseRecords(string output)
    {
        var packages = new List<DriverStorePackage>();
        var fields = new List<string>();

        void Flush()
        {
            // Published, Original, Provider, Class, [Class GUID,] Driver Version — index 4 is the
            // GUID this app has no use for.
            if (fields.Count >= 6)
            {
                packages.Add(new DriverStorePackage(fields[0], fields[1], fields[2], fields[3], fields[5]));
            }

            fields.Clear();
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            var match = FieldLine.Match(line);
            if (match.Success)
            {
                fields.Add(match.Groups[2].Value.Trim());
            }
        }

        Flush();
        return packages;
    }
}

/// <summary>
/// Pure judgement: does a device's currently-active driver package have an older duplicate sitting
/// in the store? Never names the active package itself as removable — <c>Active.PublishedName</c>
/// always differs from <c>Outdated.PublishedName</c> by construction.
/// </summary>
public static class DriverStoreDuplicateFinder
{
    public static DriverDuplicate? FindOutdatedDuplicate(IReadOnlyList<DriverStorePackage> packages, string activePublishedName)
    {
        var active = packages.FirstOrDefault(package =>
            string.Equals(package.PublishedName, activePublishedName, StringComparison.OrdinalIgnoreCase));
        if (active?.ParsedVersion is not { } activeVersion)
        {
            return null;
        }

        var outdated = packages
            .Where(package => !string.Equals(package.PublishedName, active.PublishedName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(package.OriginalName, active.OriginalName, StringComparison.OrdinalIgnoreCase)
                && package.ParsedVersion is { } version && version < activeVersion)
            .OrderBy(package => package.ParsedVersion)
            .FirstOrDefault();

        return outdated is null ? null : new DriverDuplicate(active, outdated);
    }
}
