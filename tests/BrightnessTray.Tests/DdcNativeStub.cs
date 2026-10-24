using System.Runtime.InteropServices;

namespace BrightnessTray.Windows;

// Only the cross-platform regression project compiles these stubs. Its linked production
// DdcBrightnessDevice runs unchanged against per-device state, with no Windows/hardware calls.
internal sealed class DdcNativeState
{
    internal uint HighMinimum { get; init; }
    internal uint HighMaximum { get; init; } = 100;
    internal uint HighCurrent { get; init; } = 50;
    internal uint VcpMaximum { get; set; } = 200;
    internal uint VcpCurrent { get; init; } = 100;
    internal int HighWriteError { get; set; } = 50;
    internal Queue<int> HighReadErrors { get; } = [];
    internal Queue<int> VcpReadErrors { get; } = [];
    internal List<uint> HighWrites { get; } = [];
    internal List<uint> VcpWrites { get; } = [];
    internal int VcpReads { get; set; }
}

internal sealed class SafePhysicalMonitorHandle(DdcNativeState state) : IDisposable
{
    internal DdcNativeState State => state;
    internal bool IsClosed { get; private set; }
    public void Dispose() => IsClosed = true;
}

internal static class NativeMethods
{
    internal const byte BrightnessVcpCode = 0x10;
    internal const byte PowerModeVcpCode = 0xD6;

    internal static bool GetMonitorBrightness(SafePhysicalMonitorHandle monitor,
        out uint minimum, out uint current, out uint maximum)
    {
        var state = monitor.State;
        minimum = state.HighMinimum;
        current = state.HighCurrent;
        maximum = state.HighMaximum;
        return Result(state.HighReadErrors.Count == 0 ? 0 : state.HighReadErrors.Dequeue());
    }

    internal static bool SetMonitorBrightness(SafePhysicalMonitorHandle monitor, uint brightness)
    {
        monitor.State.HighWrites.Add(brightness);
        return Result(monitor.State.HighWriteError);
    }

    internal static bool GetVCPFeatureAndVCPFeatureReply(SafePhysicalMonitorHandle monitor, byte code,
        out int type, out uint current, out uint maximum)
    {
        type = 0;
        if (code == PowerModeVcpCode) { current = 1; maximum = 5; return true; }
        var state = monitor.State;
        state.VcpReads++;
        current = state.VcpCurrent;
        maximum = state.VcpMaximum;
        return Result(state.VcpReadErrors.Count == 0 ? 0 : state.VcpReadErrors.Dequeue());
    }

    internal static bool SetVCPFeature(SafePhysicalMonitorHandle monitor, byte code, uint brightness)
    {
        monitor.State.VcpWrites.Add(brightness);
        return true;
    }

    private static bool Result(int error)
    {
        Marshal.SetLastPInvokeError(error);
        return error == 0;
    }
}
