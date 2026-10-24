using System.ComponentModel;
using System.Runtime.InteropServices;
using BrightnessTray.Core;
using BrightnessTray.Core.Displays;

namespace BrightnessTray.Windows;

internal sealed class DdcBrightnessDevice(
    string id, string name, SafePhysicalMonitorHandle handle) : IBrightnessDevice
{
    private BrightnessRange _range;
    private bool _useVcp;

    public string Id { get; } = id;
    public string Name { get; } = name;
    public string ConnectionLabel => "DDC/CI";

    public int ReadBrightness()
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        EnsureDisplayIsOn();
        if (!_useVcp && NativeMethods.GetMonitorBrightness(handle, out var minimum, out var current, out var maximum)
            && maximum > minimum)
        {
            _range = new BrightnessRange(minimum, maximum);
            return _range.ToPercent(current);
        }

        // Some monitors expose VCP 0x10 while the high-level Windows API rejects brightness.
        return ReadVcpBrightness();
    }

    public int SetBrightness(int percent)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        EnsureDisplayIsOn();
        if (_range.Maximum == 0) ReadBrightness();

        var nativeBrightness = _range.FromPercent(percent);
        if (!_useVcp && NativeMethods.SetMonitorBrightness(handle, nativeBrightness))
            return _range.ToPercent(nativeBrightness);

        if (!_useVcp)
        {
            // The two APIs can report different ranges; establish the VCP range before retrying.
            ReadVcpBrightness();
            nativeBrightness = _range.FromPercent(percent);
        }
        if (!NativeMethods.SetVCPFeature(handle, NativeMethods.BrightnessVcpCode, nativeBrightness))
            throw NativeError("Could not change brightness. Check the connection and DDC/CI in the monitor menu.");
        return _range.ToPercent(nativeBrightness);
    }

    public void Dispose() => handle.Dispose();

    private void EnsureDisplayIsOn()
    {
        // Power mode is optional. Only explicit MCCS standby/suspend/off replies prove unavailability.
        if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, NativeMethods.PowerModeVcpCode,
                out _, out var mode, out _) && mode is 2 or 3 or 4)
            throw new InvalidOperationException("The display is asleep or switched off. Turn it on, then refresh the display list.");
    }

    private int ReadVcpBrightness()
    {
        if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, NativeMethods.BrightnessVcpCode,
                out _, out var current, out var maximum))
            throw NativeError("Brightness is unavailable. Check that the monitor supports DDC/CI and that it is enabled in the monitor menu.");
        if (maximum == 0)
            throw new InvalidOperationException("The monitor reported an empty DDC/CI brightness range.");
        // Commit the mode and its range together, only after a successful probe. A transient
        // fallback read must not leave retries using the high-level range for raw VCP writes.
        _range = new BrightnessRange(0, maximum);
        _useVcp = true;
        return _range.ToPercent(current);
    }

    internal static Win32Exception NativeError(string message)
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error, $"{message} (Windows error {error})");
    }
}
