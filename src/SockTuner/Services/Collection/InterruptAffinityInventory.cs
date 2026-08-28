using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SockTuner.Models;

namespace SockTuner.Services.Collection;

/// <summary>
/// Collection layer: which devices have an interrupt affinity override, and what it is.
/// </summary>
/// <remarks>
/// Devices come from the PnP enumerator so only ones actually present are listed, and each is
/// matched to its own <c>Device Parameters\Interrupt Management</c> subkey. Read-only.
/// </remarks>
public static class InterruptAffinityInventory
{
    internal const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum";
    internal const string AffinitySubkey = @"Device Parameters\Interrupt Management\Affinity Policy";
    internal const string MsiSubkey = @"Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

    /// <summary>
    /// Device classes whose interrupt placement is worth showing. Everything else on a modern PC is
    /// either interrupt-free or not worth the risk of moving, and listing eight hundred devices
    /// would bury the handful that matter.
    /// </summary>
    private static readonly HashSet<string> InterestingClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Net", "Display", "HDC", "SCSIAdapter", "USB", "MEDIA", "Mouse", "Keyboard", "HIDClass", "System"
    };

    public static InterruptAffinityInventoryResult Read(bool onlyInteresting = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], 0, "Interrupt affinity requires Windows.");
        }

        var processors = Environment.ProcessorCount;
        try
        {
            var devices = new List<InterruptAffinityDevice>();
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, PNPClass, Present FROM Win32_PnPEntity WHERE Present = TRUE");
            foreach (var item in searcher.Get().Cast<ManagementBaseObject>())
            {
                using (item)
                {
                    var instanceId = item["DeviceID"] as string;
                    if (string.IsNullOrWhiteSpace(instanceId)) continue;

                    var deviceClass = item["PNPClass"] as string ?? string.Empty;
                    if (onlyInteresting && !InterestingClasses.Contains(deviceClass)) continue;

                    devices.Add(ReadDevice(
                        instanceId,
                        item["Name"] as string ?? instanceId,
                        deviceClass));
                }
            }

            return new(
                devices
                    .OrderByDescending(device => device.IsNetwork)
                    .ThenByDescending(device => device.HasOverride)
                    .ThenBy(device => device.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray(),
                processors,
                null);
        }
        catch (Exception exception) when (exception is ManagementException
            or UnauthorizedAccessException or COMException)
        {
            return new([], processors, exception.Message);
        }
    }

    internal static InterruptAffinityDevice ReadDevice(string instanceId, string friendlyName, string deviceClass)
    {
        var policy = InterruptPolicy.MachineDefault;
        var priority = InterruptPriority.Undefined;
        ulong? mask = null;
        bool? msi = null;

        using (var affinity = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\{instanceId}\{AffinitySubkey}"))
        {
            if (affinity is not null)
            {
                if (affinity.GetValue("DevicePolicy") is int rawPolicy && Enum.IsDefined((InterruptPolicy)rawPolicy))
                {
                    policy = (InterruptPolicy)rawPolicy;
                }

                if (affinity.GetValue("DevicePriority") is int rawPriority && Enum.IsDefined((InterruptPriority)rawPriority))
                {
                    priority = (InterruptPriority)rawPriority;
                }

                if (affinity.GetValue("AssignmentSetOverride") is byte[] raw)
                {
                    mask = ToMask(raw);
                }
            }
        }

        using (var msiKey = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\{instanceId}\{MsiSubkey}"))
        {
            if (msiKey?.GetValue("MSISupported") is int rawMsi) msi = rawMsi != 0;
        }

        return new InterruptAffinityDevice(
            instanceId,
            friendlyName,
            deviceClass,
            policy,
            priority,
            mask,
            msi,
            string.Equals(deviceClass, "Net", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The mask is a little-endian KAFFINITY; anything past 64 bits is not representable here.</summary>
    internal static ulong ToMask(byte[] raw)
    {
        var mask = 0UL;
        for (var index = 0; index < raw.Length && index < 8; index++)
        {
            mask |= (ulong)raw[index] << (8 * index);
        }

        return mask;
    }

    /// <summary>Trailing zero bytes are dropped so the value written matches what Windows writes itself.</summary>
    internal static byte[] ToBytes(ulong mask)
    {
        var full = BitConverter.GetBytes(mask);
        var length = full.Length;
        while (length > 1 && full[length - 1] == 0) length--;
        return full[..length];
    }
}
