namespace SockTuner.Tests;

public sealed class RunningApplicationPickerTests
{
    [Fact]
    public void NormalizeApplicationNames_AddsExtensionSortsAndDeduplicates()
    {
        var names = MainWindow.NormalizeApplicationNames(["Game", "game.exe", null, "", "Browser"]);

        Assert.Equal(["Browser.exe", "Game.exe"], names);
    }
}
