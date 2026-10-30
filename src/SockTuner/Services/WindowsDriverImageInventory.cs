using System.IO;
using SockTuner.Models;

namespace SockTuner.Services;

/// <summary>
/// One signed driver and the PnP device instance it serves. <see cref="InfName"/> is the exact
/// driver-store published name (e.g. <c>oem17.inf</c>) this device is currently bound to — the key
/// that correlates it against <see cref="DriverStorePackage.PublishedName"/>.
/// </summary>
public sealed record DriverImageDevice(string DriverFileName, string DeviceInstanceId, string DeviceName, string? InfName);

public sealed record DriverImageInventoryResult(IReadOnlyList<DriverImageDevice> Devices, string? Error);

/// <summary>
/// Maps a driver image (the file name an ETW capture attributes time to, e.g. <c>nvlddmkm.sys</c>)
/// back to the PnP device instance it serves, so a DPC/ISR finding naming a driver can turn into an
/// interrupt-affinity recommendation naming a device — <see cref="InterruptAffinitySpecification"/>
/// only ever takes a device instance ID, never a driver file name.
/// </summary>
internal static class WindowsDriverImageInventory
{
    internal static DriverImageInventoryResult Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], null);
        }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT DeviceName, DeviceID, DriverName, InfName FROM Win32_PnPSignedDriver WHERE DriverName IS NOT NULL");
            var devices = new List<DriverImageDevice>();
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    if (item["DriverName"] is not string path || string.IsNullOrWhiteSpace(path)
                        || item["DeviceID"] is not string deviceId || string.IsNullOrWhiteSpace(deviceId))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(path);
                    var deviceName = item["DeviceName"] as string ?? deviceId;
                    var infName = item["InfName"] as string;
                    devices.Add(new DriverImageDevice(fileName, deviceId, deviceName, infName));
                }
            }

            return new(devices, null);
        }
        catch (Exception exception) when (exception is System.Management.ManagementException
            or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return new([], $"Could not read signed driver inventory: {exception.Message}");
        }
    }
}
