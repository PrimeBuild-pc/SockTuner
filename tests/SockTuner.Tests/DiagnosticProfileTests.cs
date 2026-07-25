using SockTuner.Models;

namespace SockTuner.Tests;

public sealed class DiagnosticProfileTests
{
    [Fact]
    public void Catalog_ProvidesOrderedValidatedProfiles()
    {
        Assert.Equal(["quick", "standard", "extended"], DiagnosticProfiles.All.Select(profile => profile.Id));
        Assert.All(DiagnosticProfiles.All, profile => profile.Validate());
        Assert.True(DiagnosticProfiles.All[0].SampleCount < DiagnosticProfiles.All[1].SampleCount);
        Assert.True(DiagnosticProfiles.All[1].SampleCount < DiagnosticProfiles.All[2].SampleCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(301)]
    public void Validate_RejectsUnsafeSampleCounts(int sampleCount)
    {
        var profile = new DiagnosticProfile("test", "Test", sampleCount, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Throws<ArgumentOutOfRangeException>(profile.Validate);
    }
}
