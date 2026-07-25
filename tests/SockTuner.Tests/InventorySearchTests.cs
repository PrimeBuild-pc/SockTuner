using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class InventorySearchTests
{
    [Theory]
    [InlineData("udp")]
    [InlineData("203.0.113")]
    [InlineData("46")]
    [InlineData("C:\\Giochi")]
    [InlineData("priorità")]
    public void Matches_SearchesActualStructuredValues(string query)
    {
        var policy = new QosPolicyInfo(
            "priorità Game", "Local", 7, 1, "C:\\Giochi\\game.exe", "", 2, 0, "", 0, 0,
            "203.0.113.0/24", 0, 0, 46, -1, 0, 0, 0, "", false, "", 0, "1");

        Assert.True(InventorySearch.Matches(policy, query));
        Assert.False(InventorySearch.Matches(policy, "not-present"));
        Assert.False(InventorySearch.Matches(policy, "ThrottleBitsPerSecond"));
    }
}
