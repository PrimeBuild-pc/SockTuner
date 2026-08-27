using SockTuner.Models;
using SockTuner.Services;
using SockTuner.Services.Remediation;

namespace SockTuner.Tests;

/// <summary>
/// Which TCP template a write should target. Writing to a template no filter points at is the
/// quietest failure available: the provider accepts it, the read-back matches, nothing changes.
/// </summary>
public sealed class TcpTemplateResolverTests
{
    [Fact]
    public void TheTemplateComesFromTheFilterThatCarriesOrdinaryTraffic()
    {
        // What a stock Windows 11 machine actually reports: one filter, all TCP, to Internet.
        var resolution = WindowsTcpTemplateResolver.Resolve([Filter("Internet")], null);

        Assert.Equal("Internet", resolution.Template);
        Assert.True(resolution.FromFilter);
        Assert.Contains("sends ordinary TCP traffic to the Internet template", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ACustomTemplateIsUsedOnlyWhenAFilterActuallyPointsAtIt()
    {
        var resolution = WindowsTcpTemplateResolver.Resolve([Filter("InternetCustom")], null);

        Assert.Equal("InternetCustom", resolution.Template);
        Assert.True(resolution.FromFilter);
    }

    [Fact]
    public void ANarrowFilterDoesNotClaimOrdinaryTraffic()
    {
        // A filter bound to one port range says nothing about a connection to an arbitrary host.
        var narrow = Filter("DatacenterCustom") with { RemotePortStart = 445, RemotePortEnd = 445 };

        var resolution = WindowsTcpTemplateResolver.Resolve([narrow], null);

        Assert.Equal(WindowsTcpTemplateResolver.FallbackTemplate, resolution.Template);
        Assert.False(resolution.FromFilter);
        Assert.Contains("DatacenterCustom", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonTcpFilterIsIgnored()
    {
        var udp = Filter("Datacenter") with { Protocol = 17 };

        Assert.False(WindowsTcpTemplateResolver.Resolve([udp], null).FromFilter);
    }

    [Fact]
    public void TheWidestMatchingFilterWins()
    {
        var narrow = Filter("Compat") with { LocalPortStart = 1000, LocalPortEnd = 2000 };
        var wide = Filter("Internet");

        Assert.Equal("Internet", WindowsTcpTemplateResolver.Resolve([narrow, wide], null).Template);
    }

    [Fact]
    public void NoReadableFilterFallsBackToTheWindowsDefaultAndSaysSo()
    {
        var resolution = WindowsTcpTemplateResolver.Resolve([], "provider unavailable");

        Assert.Equal("Internet", resolution.Template);
        Assert.False(resolution.FromFilter);
        Assert.Equal("provider unavailable", resolution.Error);
        Assert.Contains("Windows default template", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdvisorNoLongerAssumesACustomTemplate()
    {
        // The original default was InternetCustom, which on a stock machine carries no traffic.
        Assert.Equal("Internet", TcpTuningAdvisor.DefaultTcpTemplate);
        Assert.Equal("Internet", new RemediationContext().TcpTemplate);
    }

    [LiveWindowsFact]
    public void TheLiveMachineMapsTrafficToATemplateSockTunerCanName()
    {
        var resolution = WindowsTcpTemplateResolver.Read();

        Assert.Null(resolution.Error);
        Assert.NotEmpty(resolution.Template);
        Assert.All(resolution.Filters, filter => Assert.NotEmpty(filter.SettingName));

        // The resolved template must be one the settings provider actually exposes, or every write
        // against it would fail to find its instance.
        var templates = WindowsGlobalSettingInventory.Read().Capabilities
            .Select(capability => capability.InstanceKey)
            .Where(key => key is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(resolution.Template, templates, StringComparer.OrdinalIgnoreCase);
    }

    private static TcpTransportFilter Filter(string template) =>
        new(template, TcpTransportFilter.Tcp, 0, 65535, 0, 65535, "*");
}
