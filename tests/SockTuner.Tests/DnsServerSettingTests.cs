using Microsoft.Win32;
using SockTuner.Models;
using SockTuner.Services;

namespace SockTuner.Tests;

public sealed class DnsServerSettingTests
{
    private const string Adapter = "{11111111-2222-3333-4444-555555555555}";
    private static readonly DnsServerSpecification Specification = new();

    [Fact]
    public void TheAddressTargetsTheChosenInterfaceOnly()
    {
        var address = Specification.ResolveAddress(Adapter);

        Assert.EndsWith(Adapter.ToUpperInvariant(), address.RegistryPath, StringComparison.Ordinal);
        Assert.Equal("NameServer", address.ValueName);
        Assert.Equal(RegistryValueKind.String, address.ValueKind);
    }

    [Fact]
    public void AnAdapterGuidIsRequired()
    {
        Assert.Throws<ArgumentException>(() => Specification.ResolveAddress(null));
        Assert.Throws<ArgumentException>(() => Specification.ResolveAddress("not-a-guid"));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("1.1.1.1,8.8.8.8")]
    [InlineData("1.1.1.1,8.8.8.8,9.9.9.9")]
    public void ACanonicalResolverListIsAccepted(string value) => Specification.Validate(value);

    [Theory]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("1.1.1.1, 8.8.8.8")]          // spaces are not the canonical form
    [InlineData("1.1.1.1,1.1.1.1")]           // duplicates would not read back
    [InlineData("1.1.1.1,8.8.8.8,9.9.9.9,8.8.4.4")]  // Windows keeps at most three
    public void AListThatWouldNotReadBackIsRefused(string value) =>
        Assert.ThrowsAny<ArgumentException>(() => Specification.Validate(value));

    [Fact]
    public void AbsentMeansDhcpAndIsAValidStateToRestore()
    {
        // Rolling back to "whatever DHCP hands out" has to be expressible, or a change to static
        // resolvers could never be undone exactly.
        Assert.True(Specification.SupportsAbsentValue);
    }

    [Fact]
    public async Task ApplyingSendsTheCanonicalListToWindows()
    {
        string? applied = null;
        var store = new DnsServerStore((_, servers) => { applied = servers; return true; });

        await store.WriteAsync(
            Specification.ResolveAddress(Adapter),
            new StoredSettingValue(true, "1.1.1.1"),
            CancellationToken.None);

        Assert.Equal("1.1.1.1", applied);
    }

    [Fact]
    public async Task RestoringDhcpSendsNoServerList()
    {
        var called = false;
        string? applied = "unset";
        var store = new DnsServerStore((_, servers) => { called = true; applied = servers; return true; });

        await store.WriteAsync(
            Specification.ResolveAddress(Adapter), StoredSettingValue.Missing, CancellationToken.None);

        Assert.True(called);
        Assert.Null(applied);
    }

    [Fact]
    public async Task AValueWindowsRefusesIsReportedRatherThanSilentlyIgnored()
    {
        var store = new DnsServerStore((_, _) => false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(
            Specification.ResolveAddress(Adapter),
            new StoredSettingValue(true, "1.1.1.1"),
            CancellationToken.None));
    }

    [Fact]
    public async Task AnInvalidListNeverReachesWindows()
    {
        var called = false;
        var store = new DnsServerStore((_, _) => { called = true; return true; });

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync(
            Specification.ResolveAddress(Adapter),
            new StoredSettingValue(true, "1.1.1.1, 8.8.8.8"),
            CancellationToken.None));
        Assert.False(called);
    }

    [Fact]
    public async Task TheStoreRefusesAddressesThatAreNotItsOwn()
    {
        var store = new DnsServerStore((_, _) => true);
        var foreign = new SettingAddress("mmcss.system-responsiveness", null, "SOFTWARE\\X", "Y", RegistryValueKind.DWord);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ReadAsync(foreign, CancellationToken.None));
    }

    [Theory]
    [InlineData("1.1.1.1 8.8.8.8", "1.1.1.1,8.8.8.8")]   // Windows also writes space separated
    [InlineData("1.1.1.1,,8.8.8.8", "1.1.1.1,8.8.8.8")]
    [InlineData("1.1.1.1,garbage", "1.1.1.1")]
    public void StoredListsAreNormalisedWhenRead(string stored, string expected) =>
        Assert.Equal(expected, DnsServerSpecification.Canonical(DnsServerSpecification.Parse(stored)));
}
