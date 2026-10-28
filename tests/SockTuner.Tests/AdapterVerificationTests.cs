using SockTuner.Models;

namespace SockTuner.Tests;

public class AdapterVerificationTests
{
    [Theory]
    [InlineData("Intel(R) Ethernet Controller I226-V", AdapterVerification.Verified)]
    [InlineData("Intel(R) Ethernet Controller I226-V #2", AdapterVerification.Verified)]
    [InlineData("realtek pcie 2.5gbe family controller", AdapterVerification.Verified)]
    [InlineData("Some Unknown Gigabit Adapter", AdapterVerification.Unverified)]
    public void KnownModelsAreVerifiedWhateverTheMarksOrInstanceSuffix(string description, AdapterVerification expected) =>
        Assert.Equal(expected, AdapterVerificationCatalog.For(description));

    [Fact]
    public void NormalizationIgnoresMarksAndCase() =>
        Assert.Equal("intel ethernet controller i226 v", AdapterVerificationCatalog.Normalize("Intel(R) Ethernet Controller I226-V #3"));
}
