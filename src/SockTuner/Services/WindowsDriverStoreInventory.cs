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
            if (process.ExitCode != 0)
            {
                return new([], $"pnputil /enum-drivers exited with code {process.ExitCode}.");
            }

            // Do not filter by driver image or device presence: a package can serve another device.
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT InfName FROM Win32_PnPSignedDriver");
            using var bindings = searcher.Get();
            var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in bindings)
            {
                using (binding)
                {
                    if (binding["InfName"] is not string name || string.IsNullOrWhiteSpace(name))
                    {
                        return new([], "A device driver binding could not be identified; cleanup is unavailable.");
                    }
                    inUse.Add(name);
                }
            }
            return inUse.Count == 0
                ? new([], "No device driver bindings were returned; cleanup is unavailable.")
                : new(ParseRecords(output), null, inUse);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException
            or System.Management.ManagementException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException)
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
/// Cleanup is independent of latency: retain the newest version and every package bound to any
/// device. Missing bindings or ambiguous metadata fail closed rather than guessing.
/// </summary>
public static class DriverStoreDuplicateFinder
{
    public static IReadOnlyList<DriverDuplicate> FindOutdatedDuplicates(
        IReadOnlyList<DriverStorePackage> packages, IReadOnlySet<string>? inUsePublishedNames)
    {
        if (inUsePublishedNames is null || inUsePublishedNames.Count == 0)
        {
            return [];
        }

        var inUse = inUsePublishedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return packages
            .Where(package => !string.IsNullOrWhiteSpace(package.OriginalName)
                && !string.IsNullOrWhiteSpace(package.ProviderName)
                && !string.IsNullOrWhiteSpace(package.ClassName))
            .GroupBy(package => (package.OriginalName.ToUpperInvariant(),
                package.ProviderName.ToUpperInvariant(), package.ClassName.ToUpperInvariant()))
            .Where(group => group.All(package => package.ParsedVersion is not null))
            .SelectMany(group =>
            {
                var newest = group.OrderByDescending(package => package.ParsedVersion).First();
                return group.Where(package => package.ParsedVersion! < newest.ParsedVersion!
                        && !inUse.Contains(package.PublishedName)
                        && Regex.IsMatch(package.PublishedName, @"\Aoem[0-9]+\.inf\z", RegexOptions.IgnoreCase))
                    .Select(package => new DriverDuplicate(newest, package));
            })
            .OrderBy(duplicate => duplicate.Outdated.OriginalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(duplicate => duplicate.Outdated.ParsedVersion)
            .ToArray();
    }
}
