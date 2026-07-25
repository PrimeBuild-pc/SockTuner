using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using SockTuner.Models;

namespace SockTuner.Services;

internal static class WindowsQosPolicyInventory
{
    private const string NamespacePath = @"\\.\root\StandardCimv2";
    private const string Query = """
        SELECT Name, Owner, NetworkProfile, Precedence, AppPathNameMatchCondition, UserMatchCondition,
               IPProtocolMatchCondition, IPPortMatchCondition, IPSrcPrefixMatchCondition,
               IPSrcPortStartMatchCondition, IPSrcPortEndMatchCondition, IPDstPrefixMatchCondition,
               IPDstPortStartMatchCondition, IPDstPortEndMatchCondition, DSCPAction,
               PriorityValue8021Action, ThrottleRateAction, MinBandwidthWeightAction,
               TemplateMatchCondition, URIMatchCondition, URIRecursiveMatchCondition,
               JobObjectMatchCondition, NetDirectPortMatchCondition, Version
        FROM MSFT_NetQosPolicySettingData
        """;

    internal static QosPolicyInventoryResult Read()
    {
        if (!OperatingSystem.IsWindows()) return new([], null);

        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(NamespacePath), new ObjectQuery(Query));
            using var results = searcher.Get();
            var policies = new List<QosPolicyInfo>(results.Count);
            var errors = new List<string>();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    try
                    {
                        policies.Add(new QosPolicyInfo(
                            String(item, "Name"), String(item, "Owner"), UInt32(item, "NetworkProfile"),
                            UInt32(item, "Precedence"), String(item, "AppPathNameMatchCondition"),
                            String(item, "UserMatchCondition"), UInt32(item, "IPProtocolMatchCondition"),
                            UInt16(item, "IPPortMatchCondition"), String(item, "IPSrcPrefixMatchCondition"),
                            UInt16(item, "IPSrcPortStartMatchCondition"), UInt16(item, "IPSrcPortEndMatchCondition"),
                            String(item, "IPDstPrefixMatchCondition"), UInt16(item, "IPDstPortStartMatchCondition"),
                            UInt16(item, "IPDstPortEndMatchCondition"), SByte(item, "DSCPAction", -1),
                            SByte(item, "PriorityValue8021Action", -1), UInt64(item, "ThrottleRateAction"),
                            Byte(item, "MinBandwidthWeightAction"), UInt32(item, "TemplateMatchCondition"),
                            String(item, "URIMatchCondition"), Boolean(item, "URIRecursiveMatchCondition"),
                            String(item, "JobObjectMatchCondition"), UInt16(item, "NetDirectPortMatchCondition"),
                            String(item, "Version")));
                    }
                    catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
                    {
                        errors.Add($"QoS policy row: {exception.Message}");
                    }
                }
            }

            return new(
                policies.OrderBy(policy => policy.Precedence).ThenBy(policy => policy.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
                errors.Count == 0 ? null : string.Join(" ", errors));
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or COMException)
        {
            return new([], exception.Message);
        }
    }

    private static string String(ManagementBaseObject item, string name) => Convert.ToString(item[name], CultureInfo.InvariantCulture) ?? string.Empty;
    private static bool Boolean(ManagementBaseObject item, string name) => item[name] is not null && Convert.ToBoolean(item[name], CultureInfo.InvariantCulture);
    private static byte Byte(ManagementBaseObject item, string name) => item[name] is null ? (byte)0 : Convert.ToByte(item[name], CultureInfo.InvariantCulture);
    private static sbyte SByte(ManagementBaseObject item, string name, sbyte fallback) => item[name] is null ? fallback : Convert.ToSByte(item[name], CultureInfo.InvariantCulture);
    private static ushort UInt16(ManagementBaseObject item, string name) => item[name] is null ? (ushort)0 : Convert.ToUInt16(item[name], CultureInfo.InvariantCulture);
    private static uint UInt32(ManagementBaseObject item, string name) => item[name] is null ? 0 : Convert.ToUInt32(item[name], CultureInfo.InvariantCulture);
    private static ulong UInt64(ManagementBaseObject item, string name) => item[name] is null ? 0 : Convert.ToUInt64(item[name], CultureInfo.InvariantCulture);
}

internal sealed record QosPolicyInventoryResult(IReadOnlyList<QosPolicyInfo> Policies, string? Error);
