using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class WindowsDriverStoreInventoryTests
{
    // A real "pnputil /enum-drivers" capture (trimmed), including a multi-line "Attributes" field
    // (the "Attested" continuation line with no label of its own) and two genuine duplicate pairs
    // this machine actually has sitting in its driver store.
    private const string RealSample = """
        Microsoft PnP Utility

        Published Name:     oem35.inf
        Original Name:      adafruitcircuitplayground.inf
        Provider Name:      Adafruit Industries LLC
        Class Name:         Ports
        Class GUID:         {4d36e978-e325-11ce-bfc1-08002be10318}
        Driver Version:     02/25/2016 6.2.2600.0
        Signer Name:        Adafruit Industries
        Attributes:         Legacy
        WHCP Version:       Unknown

        Published Name:     oem17.inf
        Original Name:      amdgpio2.inf
        Provider Name:      Advanced Micro Devices, Inc
        Class Name:         System
        Class GUID:         {4d36e97d-e325-11ce-bfc1-08002be10318}
        Driver Version:     03/22/2026 2.2.0.137
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher
        Attributes:         Universal
        WHCP Version:       Unknown

        Published Name:     oem19.inf
        Original Name:      amdgpio2.inf
        Provider Name:      Advanced Micro Devices, Inc
        Class Name:         System
        Class GUID:         {4d36e97d-e325-11ce-bfc1-08002be10318}
        Driver Version:     11/10/2025 2.2.0.136
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher
        Attributes:         Universal
        WHCP Version:       Unknown

        Published Name:     oem14.inf
        Original Name:      ovpn-dco.inf
        Provider Name:      OpenVPN, Inc
        Class Name:         Net
        Class GUID:         {4d36e972-e325-11ce-bfc1-08002be10318}
        Driver Version:     06/09/2026 2.8.3.0
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher
        Attributes:         Universal
                            Attested
        WHCP Version:       Unknown

        Published Name:     oem45.inf
        Original Name:      ovpn-dco.inf
        Provider Name:      OpenVPN, Inc
        Class Name:         Net
        Class GUID:         {4d36e972-e325-11ce-bfc1-08002be10318}
        Driver Version:     08/27/2026 2.8.7.0
        Signer Name:        Microsoft Windows Hardware Compatibility Publisher
        Attributes:         Universal
                            Attested
        WHCP Version:       Unknown

        """;

    [Fact]
    public void ParseRecords_ReadsEveryPackageDespiteTheMultiLineAttributesField()
    {
        var packages = WindowsDriverStoreInventory.ParseRecords(RealSample);

        Assert.Equal(5, packages.Count);
        Assert.Contains(packages, package => package.PublishedName == "oem35.inf" && package.OriginalName == "adafruitcircuitplayground.inf");
    }

    [Fact]
    public void ParseRecords_IsPositionalSoALocalizedLabelStillParses()
    {
        // The field *order* is the only thing pnputil guarantees across locales; the label text
        // itself (here, Russian) must never be required to match.
        const string russianLabelled = """
            Microsoft PnP Utility

            Опубликованное имя:     oem7.inf
            Исходное имя:            example.inf
            Имя поставщика:          Example Vendor
            Имя класса:              Net
            GUID класса:             {4d36e972-e325-11ce-bfc1-08002be10318}
            Версия драйвера:         01/01/2026 1.2.3.4

            """;

        var packages = WindowsDriverStoreInventory.ParseRecords(russianLabelled);

        var package = Assert.Single(packages);
        Assert.Equal("oem7.inf", package.PublishedName);
        Assert.Equal("example.inf", package.OriginalName);
        Assert.Equal(new Version(1, 2, 3, 4), package.ParsedVersion);
    }

    [Fact]
    public void ParseRecords_DriverVersionParsesTheDottedTupleIgnoringTheLocaleSensitiveDatePrefix()
    {
        var packages = WindowsDriverStoreInventory.ParseRecords(RealSample);

        var oem17 = packages.Single(package => package.PublishedName == "oem17.inf");
        Assert.Equal(new Version(2, 2, 0, 137), oem17.ParsedVersion);
    }

    [Fact]
    public void FindOutdatedDuplicate_NamesTheOlderAmdGpioPackageNeverTheActiveOne()
    {
        var packages = WindowsDriverStoreInventory.ParseRecords(RealSample);

        var duplicate = DriverStoreDuplicateFinder.FindOutdatedDuplicate(packages, activePublishedName: "oem17.inf");

        Assert.NotNull(duplicate);
        Assert.Equal("oem17.inf", duplicate.Active.PublishedName);
        Assert.Equal("oem19.inf", duplicate.Outdated.PublishedName);
        Assert.NotEqual(duplicate.Active.PublishedName, duplicate.Outdated.PublishedName);
    }

    [Fact]
    public void FindOutdatedDuplicate_FindsTheOlderOvpnPackageWhenTheNewerOneIsActive()
    {
        // oem45.inf (2.8.7.0) is the newer of the two ovpn-dco.inf packages; its own multi-line
        // "Attributes" field must not have thrown off the field count.
        var packages = WindowsDriverStoreInventory.ParseRecords(RealSample);

        var duplicate = DriverStoreDuplicateFinder.FindOutdatedDuplicate(packages, activePublishedName: "oem45.inf");

        Assert.NotNull(duplicate);
        Assert.Equal("oem14.inf", duplicate.Outdated.PublishedName);
    }

    [Fact]
    public void FindOutdatedDuplicate_NeverFlagsANewerPackageEvenWhenAnOlderOneIsTheActiveDriver()
    {
        // oem14.inf (2.8.3.0) is the OLDER ovpn-dco.inf package; if it is somehow the one bound to
        // the device (e.g. after a rollback), the newer oem45.inf must never be named as "to remove".
        var packages = WindowsDriverStoreInventory.ParseRecords(RealSample);

        var duplicate = DriverStoreDuplicateFinder.FindOutdatedDuplicate(packages, activePublishedName: "oem14.inf");

        Assert.Null(duplicate);
    }

    [Fact]
    public void FindOutdatedDuplicate_FindsNothingForADriverWithNoDuplicateAtAll()
    {
        var packages = WindowsDriverStoreInventory.ParseRecords(RealSample);

        var duplicate = DriverStoreDuplicateFinder.FindOutdatedDuplicate(packages, activePublishedName: "oem35.inf");

        Assert.Null(duplicate);
    }
}

/// <summary>Read-only live check of "pnputil /enum-drivers"; skipped unless explicitly enabled.</summary>
public sealed class WindowsDriverStoreInventoryLiveTests
{
    [LiveWindowsFact]
    public void Read_ReportsThisMachinesRealDriverStore()
    {
        var result = WindowsDriverStoreInventory.Read();

        Assert.Null(result.Error);
        Assert.NotEmpty(result.Packages);
    }
}
