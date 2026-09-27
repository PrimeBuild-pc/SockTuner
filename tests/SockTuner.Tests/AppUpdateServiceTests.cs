using System.Security.Cryptography;
using System.Text;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class AppUpdateServiceTests
{
    [Theory]
    [InlineData("1.2.0", "1.1.9", 1)]
    [InlineData("1.2.0-alpha.2", "1.2.0-alpha.10", -1)]
    [InlineData("1.2.0", "1.2.0-rc.1", 1)]
    [InlineData("1.2.0+build.2", "1.2.0+build.1", 0)]
    public void CompareVersions_FollowsSemanticVersionPrecedence(string left, string right, int sign)
    {
        Assert.Equal(sign, Math.Sign(AppUpdateService.CompareVersions(left, right)));
    }

    [Fact]
    public void SelectRelease_RespectsStableAndPreviewChannels()
    {
        const string json = """
        [
          {
            "tag_name":"v1.2.0-beta.1","name":"Beta","body":"Preview notes","html_url":"https://github.com/PrimeBuild-pc/SockTuner/releases/tag/v1.2.0-beta.1","draft":false,"prerelease":true,
            "assets":[
              {"name":"SockTuner-1.2.0-beta.1-win-x64.zip","browser_download_url":"https://github.com/PrimeBuild-pc/SockTuner/releases/download/v1.2.0-beta.1/SockTuner-1.2.0-beta.1-win-x64.zip"},
              {"name":"SockTuner-1.2.0-beta.1-win-x64.zip.sha256","browser_download_url":"https://github.com/PrimeBuild-pc/SockTuner/releases/download/v1.2.0-beta.1/SockTuner-1.2.0-beta.1-win-x64.zip.sha256"}
            ]
          },
          {
            "tag_name":"v1.1.0","name":"Stable","body":"Stable notes","html_url":"https://github.com/PrimeBuild-pc/SockTuner/releases/tag/v1.1.0","draft":false,"prerelease":false,
            "assets":[
              {"name":"SockTuner-1.1.0-win-x64.zip","browser_download_url":"https://github.com/PrimeBuild-pc/SockTuner/releases/download/v1.1.0/SockTuner-1.1.0-win-x64.zip"},
              {"name":"SockTuner-1.1.0-win-x64.zip.sha256","browser_download_url":"https://github.com/PrimeBuild-pc/SockTuner/releases/download/v1.1.0/SockTuner-1.1.0-win-x64.zip.sha256"}
            ]
          }
        ]
        """;

        Assert.Equal("1.1.0", AppUpdateService.SelectRelease(json, UpdateChannel.Stable, "1.0.0")?.Version);
        Assert.Equal("1.2.0-beta.1", AppUpdateService.SelectRelease(json, UpdateChannel.Preview, "1.0.0")?.Version);
        Assert.Null(AppUpdateService.SelectRelease(json, UpdateChannel.Stable, "1.1.0"));
    }

    [Fact]
    public void InstallationNeedsElevation_ReturnsFalseForAWritableDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"SockTuner-Update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Assert.False(AppUpdateService.InstallationNeedsElevation(Path.Combine(directory, "SockTuner.exe")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ParseChecksum_RequiresTheExpectedArchiveName()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("archive")));

        Assert.Equal(hash, AppUpdateService.ParseChecksum($"{hash}  SockTuner-1.0.0-win-x64.zip", "SockTuner-1.0.0-win-x64.zip"));
        Assert.Throws<InvalidDataException>(() => AppUpdateService.ParseChecksum(
            $"{hash}  another.zip", "SockTuner-1.0.0-win-x64.zip"));
    }
}
