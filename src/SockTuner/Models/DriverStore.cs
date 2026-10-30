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

public sealed record DriverStoreInventoryResult(IReadOnlyList<DriverStorePackage> Packages, string? Error);

/// <summary>
/// An older package for the same original driver as the one actually bound to a device — the exact
/// case <c>Active.PublishedName != Outdated.PublishedName</c> guarantees the outdated one is never
/// the package currently in use.
/// </summary>
public sealed record DriverDuplicate(DriverStorePackage Active, DriverStorePackage Outdated);
