using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace BrightnessTray.App;

// The hook never enters the shell or a brightness driver. Its UI-owned snapshot expires quickly.
internal sealed partial class TrayMouseWheel : IDisposable
{
    private const int MouseWheelMessage = 0x020A;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<int> _onWheel;
    private readonly HookProcedure _procedure;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _hook;
    private uint _threadId;
    private Exception? _startupError;
    private HitRegion? _region;
    private int _disposed;

    internal TrayMouseWheel(DispatcherQueue dispatcher, Action<int> onWheel)
    {
        _dispatcher = dispatcher;
        _onWheel = onWheel;
        _procedure = HandleMouse;
        _thread = new Thread(Run) { IsBackground = true, Name = "BrightnessTray tray wheel" };
        _thread.Start();
        _ready.Wait();
        if (_startupError is not null)
        {
            _thread.Join();
            _ready.Dispose();
            throw new InvalidOperationException("Could not listen for scrolling over the tray icon.", _startupError);
        }
    }

    internal void UpdateRegion(Rectangle? bounds, IntPtr shellWindow) => Volatile.Write(ref _region,
        bounds is { Width: > 0, Height: > 0 } && shellWindow != IntPtr.Zero
            ? new HitRegion(bounds.Value, shellWindow, Environment.TickCount64) : null);

    private void Run()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // Ensure WM_QUIT can be posted immediately after initialization.
            SetThreadDpiAwarenessContext(new IntPtr(-4)); // Per-monitor v2 coordinates, matching the WinUI UI thread.
            _hook = SetWindowsHookEx(14, _procedure, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            _ready.Set();
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception exception) { _startupError = exception; }
        finally
        {
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            _ready.Set();
        }
    }

    private IntPtr HandleMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt64() == MouseWheelMessage && Volatile.Read(ref _disposed) == 0)
        {
            var input = Marshal.PtrToStructure<MouseHookData>(data);
            var region = Volatile.Read(ref _region);
            var delta = unchecked((short)(input.MouseData >> 16));
            if (region is not null && IsHit(region.Bounds, region.UpdatedAt, Environment.TickCount64,
                    input.Position.X, input.Position.Y, delta)
                && GetAncestor(WindowFromPoint(input.Position), 2) == region.ShellWindow
                && IsWindowVisible(region.ShellWindow)
                && _dispatcher.TryEnqueue(() =>
                {
                    if (Volatile.Read(ref _disposed) == 0) _onWheel(delta);
                })) return new IntPtr(1);
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    internal static bool IsHit(Rectangle bounds, long updatedAt, long now, int x, int y, int delta) =>
        delta != 0 && now >= updatedAt && now - updatedAt <= 250 && bounds.Contains(x, y);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _region, null);
        PostThreadMessage(_threadId, 0x0012, UIntPtr.Zero, IntPtr.Zero);
        _thread.Join();
        _ready.Dispose();
    }

    private sealed record HitRegion(Rectangle Bounds, IntPtr ShellWindow, long UpdatedAt);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X; internal int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        internal NativePoint Position;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal IntPtr Window;
        internal uint Message;
        internal UIntPtr WParam;
        internal IntPtr LParam;
        internal uint Time;
        internal NativePoint Point;
        internal uint Private;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int kind, HookProcedure procedure, IntPtr module, uint thread);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint first, uint last);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint first, uint last, uint remove);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint thread, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
