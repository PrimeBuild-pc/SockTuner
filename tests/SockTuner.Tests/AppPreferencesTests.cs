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
            AppPreferences.Save(path, new(
                9,
                SelectedSection: "Wi-Fi diagnostics",
                WifiInterfaceId: "radio-guid",
                UpdateChannel: "Preview",
                LastUpdateCheckAt: DateTimeOffset.Parse("2026-01-02T03:04:05Z")));

            var loaded = AppPreferences.Load(path);
            Assert.Equal(9, loaded.LogFileMegabytes);
            Assert.Equal("Wi-Fi diagnostics", loaded.SelectedSection);
            Assert.Equal("radio-guid", loaded.WifiInterfaceId);
            Assert.Equal("Preview", loaded.UpdateChannel);
            Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), loaded.LastUpdateCheckAt);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void Validate_FallsBackToTheStableUpdateChannel()
    {
        Assert.Equal("Stable", AppPreferences.Validate(new(UpdateChannel: "invalid")).UpdateChannel);
    }

    [Fact]
    public void WriteConsent_IsNotGrantedUntilAcceptedAndSurvivesARoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"SockTuner-{Guid.NewGuid():N}", "preferences.json");
        try
        {
            Assert.False(WriteConsent.IsAccepted(new UserPreferences()));

            var accepted = WriteConsent.Accept(new UserPreferences());
            Assert.True(WriteConsent.IsAccepted(accepted));
            Assert.NotNull(accepted.WriteConsentAcceptedAt);

            AppPreferences.Save(path, accepted);
            Assert.True(WriteConsent.IsAccepted(AppPreferences.Load(path)));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void WriteConsent_FromAnEarlierVersionIsNotAccepted()
    {
        var stale = new UserPreferences(AcceptedWriteConsentVersion: "alpha-0");

        Assert.False(WriteConsent.IsAccepted(stale));
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
