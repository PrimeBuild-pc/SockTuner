using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class WindowsNdisInventoryTests
{
    [Fact]
    public void FormatValue_PreservesCommonRegistryValueShapes()
    {
        Assert.Equal("—", WindowsNdisInventory.FormatValue(null));
        Assert.Equal("010AFF", WindowsNdisInventory.FormatValue(new byte[] { 1, 10, 255 }));
        Assert.Equal("one, two", WindowsNdisInventory.FormatValue(new[] { "one", "two" }));
        Assert.Equal("42", WindowsNdisInventory.FormatValue(42));
    }

    [Theory]
    [InlineData("0000", true)]
    [InlineData("42", true)]
    [InlineData("Properties", false)]
    [InlineData("", false)]
    public void IsAdapterInstanceKey_RejectsNonAdapterClassChildren(string keyName, bool expected)
    {
        Assert.Equal(expected, WindowsNdisInventory.IsAdapterInstanceKey(keyName));
    }

    [Fact]
    public void FindMatchingAdapterKey_NormalizesGuidAndSurfacesNoMatch()
    {
        var candidates = new[]
        {
            (KeyName: "0000", AdapterId: (string?)"{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}"),
            (KeyName: "0001", AdapterId: (string?)"{11111111-2222-3333-4444-555555555555}")
        };

        Assert.Equal("0000", WindowsNdisInventory.FindMatchingAdapterKey(
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", candidates));
        Assert.Null(WindowsNdisInventory.FindMatchingAdapterKey(
            "FFFFFFFF-BBBB-CCCC-DDDD-EEEEEEEEEEEE", candidates));
    }
}
