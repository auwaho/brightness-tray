using System.ComponentModel;
using System.Runtime.InteropServices;
using BrightnessTray.Core.Displays;

namespace BrightnessTray.Windows;

public sealed class WindowsDisplayDiscovery : IDisplayDiscovery
{
    public DisplayDiscoveryResult Discover()
    {
        var devices = new List<IBrightnessDevice>();
        var issues = new List<DisplayIssue>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var internalInstances = DiscoverWmi(devices, issues, names);
        try
        {
            DiscoverDdc(devices, issues, internalInstances, names);
            return new DisplayDiscoveryResult(devices, issues);
        }
        catch
        {
            foreach (var device in devices) device.Dispose();
            throw;
        }
    }

    private static HashSet<string> DiscoverWmi(List<IBrightnessDevice> devices, List<DisplayIssue> issues,
        Dictionary<string, string> names)
    {
        var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var session = new WmiSession();
            ReadWmiNames(session, names);
            // Keep known inactive panels as disabled rows; readiness is checked when brightness is read.
            session.Query("SELECT InstanceName, Level FROM WmiMonitorBrightness", item =>
            {
                if (WmiSession.ReadValue(item, "InstanceName") is not string instance || instances.Contains(NormalizeInstance(instance)))
                    return;
                var name = names.GetValueOrDefault(NormalizeInstance(instance), "Built-in display");
                var levels = WmiSession.ReadValue(item, "Level") is Array values
                    ? values.Cast<object>().Select(Convert.ToByte).Where(level => level <= 100).Distinct().Order().ToArray()
                    : [];
                devices.Add(new WmiBrightnessDevice(instance, name, levels));
                instances.Add(NormalizeInstance(instance));
            });
        }
        catch (COMException exception) when (IsAbsentWmiClass(exception))
        {
            // Most desktop computers have no internal-panel WMI provider.
        }
        catch (Exception exception)
        {
            issues.Add(new DisplayIssue("Built-in displays", $"Could not read WMI brightness: {exception.Message}"));
        }
        return instances;
    }

    private static void ReadWmiNames(WmiSession session, Dictionary<string, string> names)
    {
        try
        {
            session.Query("SELECT InstanceName, UserFriendlyName FROM WmiMonitorID", item =>
            {
                if (WmiSession.ReadValue(item, "InstanceName") is not string instance
                    || WmiSession.ReadValue(item, "UserFriendlyName") is not Array characters) return;
                var name = new string(characters.Cast<object>().Select(value => (char)Convert.ToUInt16(value))
                    .TakeWhile(value => value != '\0').ToArray()).Trim();
                if (name.Length > 0) names[NormalizeInstance(instance)] = name;
            });
        }
        catch (COMException)
        {
            // Friendly names are optional; brightness does not depend on monitor EDID exposure.
        }
    }

    private static void DiscoverDdc(List<IBrightnessDevice> devices, List<DisplayIssue> issues,
        HashSet<string> internalInstances, Dictionary<string, string> names)
    {
        var monitors = new List<nint>();
        bool Collect(nint monitor, nint context, ref NativeMethods.NativeRect rectangle, nint data)
        {
            monitors.Add(monitor);
            return true;
        }
        if (!NativeMethods.EnumDisplayMonitors(0, 0, Collect, 0))
            issues.Add(new DisplayIssue("Displays", DdcBrightnessDevice.NativeError("Could not enumerate Windows displays.").Message));

        foreach (var monitor in monitors)
        {
            var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                issues.Add(new DisplayIssue("Display", DdcBrightnessDevice.NativeError("Could not get display information.").Message));
                continue;
            }
            var endpoints = ReadDisplayEndpoints(info.DeviceName);
            // The two native enumeration APIs do not promise matching physical-monitor order.
            // Suppress WMI duplicates only when the complete logical display is known to be internal.
            if (AllEndpointsAreInternal(endpoints, internalInstances)) continue;
            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count))
            {
                issues.Add(new DisplayIssue(info.DeviceName, DdcBrightnessDevice.NativeError("DDC/CI is unavailable for this connection.").Message));
                continue;
            }
            if (count == 0)
            {
                issues.Add(new DisplayIssue(info.DeviceName, "The driver does not expose a physical monitor for DDC/CI."));
                continue;
            }

            var physicalMonitors = new NativeMethods.PhysicalMonitor[checked((int)count)];
            try
            {
                if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(monitor, count, physicalMonitors))
                {
                    issues.Add(new DisplayIssue(info.DeviceName,
                        DdcBrightnessDevice.NativeError("Could not open the physical monitor for DDC/CI.").Message));
                    continue;
                }
                var matches = DisplayEndpointMatcher.Match(
                    physicalMonitors.Select(physical => physical.Description ?? string.Empty).ToArray(), endpoints);
                for (var index = 0; index < physicalMonitors.Length; index++)
                {
                    var physical = physicalMonitors[index];
                    var endpoint = matches[index];
                    // A mirrored logical display can contain both a WMI panel and an external monitor.
                    if (endpoint is not null && internalInstances.Contains(NormalizeInstance(endpoint.InterfaceName)))
                        continue;
                    // EDID names are more useful than the driver's generic PnP description.
                    // Prefer the EDID name from the matched native endpoint.
                    var name = endpoint is not null ? names.GetValueOrDefault(NormalizeInstance(endpoint.InterfaceName)) : null;
                    name ??= !string.IsNullOrWhiteSpace(physical.Description) ? physical.Description.Trim()
                        : endpoint?.Name ?? info.DeviceName;
                    var identity = endpoint?.InterfaceName;
                    if (string.IsNullOrWhiteSpace(identity)) identity = info.DeviceName;
                    var handle = new SafePhysicalMonitorHandle(physical.Handle);
                    physicalMonitors[index].Handle = 0; // Ownership moves from the native array to SafeHandle.
                    var device = new DdcBrightnessDevice($"ddc:{identity}:{index}", name, handle);
                    // The service reads availability into a disabled row instead of dropping the monitor.
                    devices.Add(device);
                }
            }
            finally
            {
                // Releases any handles returned before native failure or managed discovery failure.
                foreach (var physical in physicalMonitors)
                    if (physical.Handle != 0) NativeMethods.DestroyPhysicalMonitor(physical.Handle);
            }
        }
    }

    private static List<DisplayEndpoint> ReadDisplayEndpoints(string adapterName)
    {
        var endpoints = new List<DisplayEndpoint>();
        for (uint index = 0; ; index++)
        {
            var device = new NativeMethods.DisplayDevice { Size = (uint)Marshal.SizeOf<NativeMethods.DisplayDevice>() };
            if (!NativeMethods.EnumDisplayDevices(adapterName, index, ref device, NativeMethods.DeviceInterfaceName)) break;
            if ((device.StateFlags & NativeMethods.AttachedToDesktop) != 0)
                endpoints.Add(new DisplayEndpoint(device.DeviceString.Trim(), device.DeviceId));
        }
        return endpoints;
    }

    private static bool AllEndpointsAreInternal(List<DisplayEndpoint> endpoints, HashSet<string> internalInstances) =>
        endpoints.Count > 0 && endpoints.All(endpoint => internalInstances.Contains(NormalizeInstance(endpoint.InterfaceName)));

    internal static bool? IsDisplayAttached(string instanceName)
    {
        var monitors = new List<nint>();
        bool Collect(nint monitor, nint context, ref NativeMethods.NativeRect rectangle, nint data)
        {
            monitors.Add(monitor);
            return true;
        }
        if (!NativeMethods.EnumDisplayMonitors(0, 0, Collect, 0)) return null;

        var incomplete = false;
        var identity = NormalizeInstance(instanceName);
        foreach (var monitor in monitors)
        {
            var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                incomplete = true;
                continue;
            }
            var endpoints = ReadDisplayEndpoints(info.DeviceName);
            if (endpoints.Count == 0 || endpoints.Any(endpoint => string.IsNullOrWhiteSpace(endpoint.InterfaceName)))
                incomplete = true;
            if (endpoints.Any(endpoint => string.Equals(identity, NormalizeInstance(endpoint.InterfaceName),
                    StringComparison.OrdinalIgnoreCase))) return true;
        }
        // Missing driver identity data is inconclusive, not evidence that a connected panel is off.
        return incomplete ? null : false;
    }

    private static string NormalizeInstance(string instance)
    {
        if (instance.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            var parts = instance[4..].Split('#');
            if (parts.Length >= 3) return string.Join('\\', parts.Take(3));
        }
        // WMI appends a provider instance suffix to the PnP monitor instance ID.
        var separator = instance.LastIndexOf('_');
        return separator >= 0 && uint.TryParse(instance[(separator + 1)..], out _) ? instance[..separator] : instance;
    }

    private static bool IsAbsentWmiClass(COMException exception) =>
        exception.HResult is unchecked((int)0x80041010) or unchecked((int)0x80041002);

}
