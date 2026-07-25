using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class WindowsBindingInventoryTests
{
    [Theory]
    [InlineData("{DBE23C40-A216-4351-BC0F-CBF9519BC5CE}::ms_tcpip")]
    [InlineData("DBE23C40-A216-4351-BC0F-CBF9519BC5CE")]
    public void TryParseAdapterId_AcceptsCimInstanceShapes(string instanceId)
    {
        Assert.True(WindowsBindingInventory.TryParseAdapterId(instanceId, out var adapterId));
        Assert.Equal(Guid.Parse("DBE23C40-A216-4351-BC0F-CBF9519BC5CE"), adapterId);
    }

    [Fact]
    public void BindingDisplays_PreserveStateAndRawCharacteristics()
    {
        var binding = new NetworkBindingInfo(
            Guid.Empty, "Ethernet", "adapter", "ms_tcpip", "Internet Protocol Version 4",
            "Tcpip", true, 132, "class", "Transport", 1);

        Assert.Equal("Enabled", binding.StateDisplay);
        Assert.Equal("0x00000084", binding.CharacteristicsDisplay);
        Assert.Equal("Disabled", (binding with { Enabled = false }).StateDisplay);
    }
}
