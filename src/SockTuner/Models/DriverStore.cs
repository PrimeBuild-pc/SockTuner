namespace SockTuner.Models;

/// <summary>
/// One driver package as <c>pnputil /enum-drivers</c> reports it. <see cref="DriverVersionRaw"/> is
/// kept as-is ("MM/DD/YYYY A.B.C.D") rather than parsed into a date, because the date portion's
/// format is locale-dependent on a non-English Windows install and the version tuple alone is
/// enough to rank packages honestly.
/// </summary>
public sealed record DriverStorePackage(
    string PublishedName, string OriginalName, string ProviderName, string ClassName, string DriverVersionRaw)
{
    public Version? ParsedVersion => DriverVersionRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts
        && Version.TryParse(parts[^1], out var version)
        ? version
        : null;
}

public sealed record DriverStoreInventoryResult(
    IReadOnlyList<DriverStorePackage> Packages, string? Error,
    IReadOnlySet<string>? InUsePublishedNames = null);

/// <summary>
/// An unused older package and the newest retained version of the same driver family.
/// Eligibility must be revalidated against every device binding immediately before removal.
/// </summary>
public sealed record DriverDuplicate(
    [property: System.Text.Json.Serialization.JsonPropertyName("Active")] DriverStorePackage Retained,
    DriverStorePackage Outdated)
{
    public string Summary => $"{Outdated.OriginalName}: {Outdated.PublishedName} ({Outdated.ParsedVersion}) → keep {Retained.PublishedName} ({Retained.ParsedVersion})";
}
