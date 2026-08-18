using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

/// <summary>
/// Read-only checks against the machine's real drivers. Skipped unless
/// SOCKTUNER_LIVE_INVENTORY=1; they validate that the WQL projection and the parsing of the
/// driver's advertised constraints survive contact with an actual NIC.
/// </summary>
public sealed class WindowsAdapterCapabilityInventoryLiveTests
{
    [LiveWindowsFact]
    public void Read_ReturnsDriverAdvertisedCapabilitiesWithUsableConstraints()
    {
        var result = WindowsAdapterCapabilityInventory.Read();

        Assert.Null(result.Error);
        Assert.NotEmpty(result.Capabilities);

        foreach (var capability in result.Capabilities)
        {
            Assert.NotEqual(Guid.Empty, capability.AdapterId);
            Assert.False(string.IsNullOrWhiteSpace(capability.Keyword));
            Assert.False(string.IsNullOrWhiteSpace(capability.DisplayName));

            // Whatever the driver reports as current must itself satisfy the constraints the
            // driver advertises; if it does not, our parsing of those constraints is wrong.
            if (capability.IsEnumerated || capability.IsNumericRange)
            {
                capability.Validate(capability.CurrentValue);
            }
        }
    }

    [LiveWindowsFact]
    public void Read_ExposesBothEnumeratedAndNumericKeywordsOnAPhysicalAdapter()
    {
        var capabilities = WindowsAdapterCapabilityInventory.Read().Capabilities;

        Assert.Contains(capabilities, capability => capability.IsEnumerated);
        Assert.Contains(capabilities, capability => capability.IsNumericRange);
        Assert.Contains(capabilities, capability => capability.DefaultValue is not null);
    }

    [LiveWindowsFact]
    public void Read_CharacterisesMostKeywordsThisMachineAdvertises()
    {
        var keywords = WindowsAdapterCapabilityInventory.Read().Capabilities
            .Select(capability => capability.Keyword)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var uncharacterised = keywords.Where(keyword => !NicKeywordCatalog.IsCharacterised(keyword)).ToArray();

        // Not a hard requirement — unknown keywords are handled safely as high risk — but a
        // large gap means the catalog has drifted behind the hardware we actually support.
        Assert.True(
            uncharacterised.Length * 2 < keywords.Length,
            $"Uncharacterised keywords: {string.Join(", ", uncharacterised)}");
    }
}
