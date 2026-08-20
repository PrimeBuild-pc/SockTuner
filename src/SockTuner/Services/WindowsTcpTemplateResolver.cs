using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// Answers the question a TCP write depends on: which template does this machine's traffic
/// actually use?
/// </summary>
/// <remarks>
/// Windows keeps several TCP templates and one set of transport filters mapping traffic onto them.
/// Writing to the wrong template is the worst kind of failure available here — the provider accepts
/// the value, the read-back matches, and nothing changes. On a stock Windows 11 machine there is a
/// single filter sending all TCP to <c>Internet</c>, while <c>InternetCustom</c> carries nothing;
/// a tool that assumes the Custom template is tuning an empty room.
/// </remarks>
public static class WindowsTcpTemplateResolver
{
    /// <summary>Windows' own default when no filter claims ordinary traffic.</summary>
    public const string FallbackTemplate = "Internet";

    internal const string ClassName = "MSFT_NetTransportFilter";

    public static TcpTemplateResolution Read() => Read(new ManagementScope(WindowsGlobalSettingInventory.NamespacePath));

    internal static TcpTemplateResolution Read(ManagementScope scope)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Resolve([], null);
        }

        try
        {
            var filters = new List<TcpTransportFilter>();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM {ClassName}"));
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    filters.Add(new TcpTransportFilter(
                        WindowsGlobalSettingInventory.Text(item["SettingName"]),
                        Number(item, "Protocol"),
                        Number(item, "LocalPortStart"),
                        Number(item, "LocalPortEnd"),
                        Number(item, "RemotePortStart"),
                        Number(item, "RemotePortEnd"),
                        WindowsGlobalSettingInventory.Text(item["DestinationPrefix"])));
                }
            }

            return Resolve(filters, null);
        }
        catch (Exception exception) when (exception is ManagementException
            or UnauthorizedAccessException or COMException)
        {
            return Resolve([], exception.Message);
        }
    }

    /// <summary>
    /// Picks the template ordinary internet traffic lands on: the widest TCP filter that covers an
    /// arbitrary host on an arbitrary port. Pure, so the choice is testable without a CIM call.
    /// </summary>
    public static TcpTemplateResolution Resolve(IReadOnlyList<TcpTransportFilter> filters, string? error)
    {
        var match = filters
            .Where(filter => filter.CoversOrdinaryTraffic)
            .OrderByDescending(filter => filter.Coverage)
            .ThenBy(filter => filter.SettingName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (match is not null)
        {
            return new TcpTemplateResolution(
                match.SettingName, true, filters,
                $"A transport filter sends ordinary TCP traffic to the {match.SettingName} template — {match.Summary}.",
                error);
        }

        return new TcpTemplateResolution(
            FallbackTemplate, false, filters,
            filters.Count == 0
                ? $"No transport filter was readable, so the Windows default template {FallbackTemplate} is assumed."
                : $"No filter covers arbitrary hosts and ports, so the Windows default template {FallbackTemplate} is "
                    + $"assumed. {filters.Count} filter(s) were read: " + string.Join("; ", filters.Select(item => item.Summary)),
            error);
    }

    private static uint Number(ManagementBaseObject item, string property) =>
        item[property] is { } value ? Convert.ToUInt32(value, CultureInfo.InvariantCulture) : 0;
}
