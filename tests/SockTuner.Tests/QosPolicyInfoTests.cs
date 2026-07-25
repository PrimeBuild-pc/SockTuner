using SockTuner.Models;

namespace SockTuner.Tests;

public sealed class QosPolicyInfoTests
{
    [Fact]
    public void Displays_CombineTypedConditionsAndActions()
    {
        var policy = new QosPolicyInfo(
            "Game", "Local", 7, 10, "game.exe", "", 2, 3074, "", 0, 0,
            "203.0.113.0/24", 3000, 4000, 46, 5, 10_000_000, 20, 0, "", false, "", 0, "1.0");

        Assert.Equal("All", policy.ProfileDisplay);
        Assert.Equal("App game.exe; Protocol UDP; Port 3074; Destination 203.0.113.0/24; destination ports 3000–4000", policy.ConditionsDisplay);
        Assert.Equal("DSCP 46; 802.1p 5; Throttle 10 Mbps; Minimum bandwidth 20%", policy.ActionsDisplay);
    }

    [Fact]
    public void Displays_PreserveEmptyAndUnknownValues()
    {
        var policy = new QosPolicyInfo(
            "Empty", "", 8, 0, "", "", 99, 0, "", 0, 0, "", 0, 0,
            -1, -1, 0, 0, 0, "", false, "", 0, "");

        Assert.Equal("Flags 0x8", policy.ProfileDisplay);
        Assert.Equal("Protocol Value 99", policy.ConditionsDisplay);
        Assert.Equal("No action", policy.ActionsDisplay);
    }
}
