using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using BrightnessTray.Core.Diagnostics;
using BrightnessTray.Core.Displays;

namespace BrightnessTray.Windows;

// Called under BrightnessService's lifetime gate on a worker thread, including the short pauses.
internal sealed class RetryingBrightnessDevice(
    IBrightnessDevice device, DiagnosticLog? diagnostics = null, Action<TimeSpan>? delay = null) : IBrightnessDevice
{
    public string Id => device.Id;
    public string Name => device.Name;
    public string ConnectionLabel => device.ConnectionLabel;

    public int ReadBrightness() => Execute("Read brightness", device.ReadBrightness);

    // Retry an absolute value, never a wheel delta. The row keeps newer input pending separately.
    public int SetBrightness(int percent) => Execute("Set brightness", () => device.SetBrightness(percent));

    public void Dispose() => device.Dispose();

    private int Execute(string operation, Func<int> invoke)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = invoke();
                if (attempt > 1) diagnostics?.Write(operation, Name, detail: $"Recovered on attempt {attempt}/3");
                return result;
            }
            catch (Exception exception) when (attempt < 3 && IsTransient(exception))
            {
                var pause = TimeSpan.FromMilliseconds(attempt * 60);
                diagnostics?.Write(operation, Name, exception, $"Retry {attempt}/2 in {pause.TotalMilliseconds:0} ms");
                (delay ?? Thread.Sleep)(pause);
            }
        }
    }

    internal static bool IsTransient(Exception exception)
    {
        // COM dispatch and schema preparation can wrap the original native error.
        for (var depth = 0; depth < 4; depth++)
        {
            if (exception is Win32Exception native) return IsTransientCode(native.NativeErrorCode);
            if (exception is COMException) return IsTransientCode(exception.HResult);
            if (exception is not (TargetInvocationException or InvalidOperationException)
                || exception.InnerException is null) return false;
            exception = exception.InnerException;
        }
        return false;
    }

    private static bool IsTransientCode(int code) => unchecked((uint)code) is
        121 or 170 or 1237 or 1460 or // Semaphore timeout, busy, retry, timeout.
        0x80070079 or 0x800700AA or 0x800704D5 or 0x800705B4 or // HRESULT_FROM_WIN32 equivalents.
        0x80010109 or 0x8001010A or // RPC_E_RETRY / RPC_E_SERVERCALL_RETRYLATER.
        0xC0262582 or 0xC0262583 or 0xC026258B; // I2C transmit/receive or corrupted DDC checksum.
}
