using SockTuner.Persistence;

namespace SockTuner.Tests;

public sealed class AppPreferencesTests
{
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(8, 8)]
    [InlineData(100, 64)]
    public void Validate_ClampsLogRetention(int requested, int expected)
    {
        Assert.Equal(expected, AppPreferences.Validate(new(requested)).LogFileMegabytes);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsValidatedPreference()
    {
        var path = Path.Combine(Path.GetTempPath(), $"SockTuner-{Guid.NewGuid():N}", "preferences.json");
        try
        {
            AppPreferences.Save(path, new(9));

            Assert.Equal(9, AppPreferences.Load(path).LogFileMegabytes);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void Load_InvalidJsonFallsBackToDefault()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not json");
            Assert.Equal(new UserPreferences(), AppPreferences.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
