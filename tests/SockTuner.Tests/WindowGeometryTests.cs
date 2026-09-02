using SockTuner.Persistence;

namespace SockTuner.Tests;

/// <summary>
/// Restoring a window position is only safe when the position still exists. These cover the case
/// that actually strands a user: a geometry saved on a monitor that has since been unplugged.
/// </summary>
public sealed class WindowGeometryTests
{
    // A single 1920x1080 monitor at the origin.
    private const double Left = 0;
    private const double Top = 0;
    private const double Width = 1920;
    private const double Height = 1080;

    private static bool Fits(WindowGeometry geometry) => geometry.FitsWithin(Left, Top, Width, Height);

    [Fact]
    public void AWindowFullyOnScreenIsRestored() =>
        Assert.True(Fits(new(100, 100, 1320, 820, Maximized: false)));

    [Fact]
    public void AWindowHangingSlightlyOffTheEdgeIsStillRestored() =>
        // Normal and recoverable: the title bar is reachable, so this must not be discarded.
        Assert.True(Fits(new(1500, 900, 1320, 820, Maximized: false)));

    [Fact]
    public void AWindowOnAMonitorThatIsGoneIsDiscarded()
    {
        // Saved on a second display to the right that is no longer attached.
        Assert.False(Fits(new(3000, 200, 1320, 820, Maximized: false)));

        // And one saved on a display above.
        Assert.False(Fits(new(200, -2000, 1320, 820, Maximized: false)));
    }

    [Fact]
    public void AWindowLeftOfTheVirtualScreenIsDiscarded() =>
        Assert.False(Fits(new(-1400, 100, 1320, 820, Maximized: false)));

    [Fact]
    public void AGeometrySmallerThanTheWindowMinimumIsNotRestored() =>
        Assert.False(Fits(new(100, 100, 300, 200, Maximized: false)));

    [Fact]
    public void ValidateRaisesASavedSizeBackToTheWindowMinimum()
    {
        var validated = AppPreferences.Validate(new(Window: new(10, 10, 200, 150, Maximized: false)));

        Assert.Equal(WindowGeometry.MinimumWidth, validated.Window!.Width);
        Assert.Equal(WindowGeometry.MinimumHeight, validated.Window.Height);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ValidateDropsANonFiniteGeometryRatherThanRestoringIt(double corrupt) =>
        // A hand-edited or truncated preferences file must not be able to place the window nowhere.
        Assert.Null(AppPreferences.Validate(new(Window: new(corrupt, 10, 1320, 820, Maximized: false))).Window);

    [Fact]
    public void ValidateKeepsAMaximisedFlagWithItsRestoreSize()
    {
        var validated = AppPreferences.Validate(new(Window: new(10, 20, 1400, 900, Maximized: true)));

        Assert.True(validated.Window!.Maximized);
        Assert.Equal(1400, validated.Window.Width);
    }

    [Fact]
    public void GeometrySurvivesASaveAndLoadRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"SockTuner-{Guid.NewGuid():N}", "preferences.json");
        try
        {
            AppPreferences.Save(path, new(Window: new(120, 80, 1500, 950, Maximized: true)));

            var loaded = AppPreferences.Load(path);

            Assert.Equal(new WindowGeometry(120, 80, 1500, 950, Maximized: true), loaded.Window);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path)!))
            {
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            }
        }
    }
}
