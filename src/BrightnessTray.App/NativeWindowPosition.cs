using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BrightnessTray.App;

internal static partial class NativeWindowPosition
{
    public static double GetScale(IntPtr window) => GetDpiForWindow(window) / 96.0;
    internal const uint NoBorderColor = 0xFFFFFFFE;
    // A static reference keeps the native callback alive until each window removes it on destruction.
    private static readonly SubclassProcedure FrameProcedure = HandleFrameMessage;
    private static readonly UIntPtr FrameSubclassId = new(1);

    public static void ConfigureFlyoutFrame(IntPtr window)
    {
        // WinUI restores dialog-frame styles; override their client inset instead of repeatedly clearing them.
        if (!SetWindowSubclass(window, FrameProcedure, FrameSubclassId, UIntPtr.Zero))
            throw new InvalidOperationException("Could not configure the flyout's native frame.");
        // DWM provides antialiased window corners and the standard Windows shadow; XAML draws the stroke.
        var renderingPolicy = 2u;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, 2, ref renderingPolicy, sizeof(uint)));
        var preference = 2u;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, 33, ref preference, sizeof(uint)));
        var borderColor = NoBorderColor;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, 34, ref borderColor, sizeof(uint)));
        var disableTransitions = 1u;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, 3, ref disableTransitions, sizeof(uint)));
        Marshal.ThrowExceptionForHR(ConfigureTransparentHost(window));
        if (!SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x0037)) // SWP_FRAMECHANGED, no move/size/z-order/activation.
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private static int ConfigureTransparentHost(IntPtr window)
    {
        // A transparent composition brush alone leaves the HWND's client surface opaque.
        // An empty blur region enables per-pixel alpha without adding another desktop blur.
        var region = CreateRectRgn(-2, -2, -1, -1);
        if (region == IntPtr.Zero) return unchecked((int)0x80004005);
        try
        {
            var blur = new BlurBehind { Flags = 3, Enabled = 1, Region = region };
            return DwmEnableBlurBehindWindow(window, ref blur);
        }
        finally { DeleteObject(region); }
    }

    private static IntPtr HandleFrameMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData)
    {
        if (message == 0x0083) return IntPtr.Zero; // WM_NCCALCSIZE: the proposed window rectangle is the client rectangle.
        if (message == 0x031E) ConfigureTransparentHost(window); // WM_DWMCOMPOSITIONCHANGED: DWM resets alpha policy.
        if (message == 0x0082) RemoveWindowSubclass(window, FrameProcedure, subclassId); // WM_NCDESTROY.
        return DefSubclassProc(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    internal static IntPtr ForegroundWindow => GetForegroundWindow();
    internal static bool TrySetForeground(IntPtr window) => ForegroundWindow == window || SetForegroundWindow(window);
    internal static void KeepBelowTaskbar(IntPtr window, int x, int y)
    {
        // Claim focus while cloaked, then let the taskbar conceal the lower part of the sliding window.
        var shellWindow = GetAncestor(WindowFromPoint(new NativePoint { X = x, Y = y }), 2);
        var name = new StringBuilder(64);
        GetClassName(shellWindow, name, name.Capacity);
        if (name.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) return;
        if (!SetWindowPos(window, shellWindow, 0, 0, 0, 0, 0x0013))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    internal static bool IsTaskbarPointerDown
    {
        get
        {
            if ((GetAsyncKeyState(1) & 0x8000) == 0) return false;
            if (!GetCursorPos(out var pointer)) return false;
            // Focus may still be transitioning; inspect the shell window under the pointer instead.
            var shellWindow = GetAncestor(WindowFromPoint(pointer), 2);
            var name = new StringBuilder(64);
            GetClassName(shellWindow, name, name.Capacity);
            var className = name.ToString();
            return className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                || className.Contains("Overflow", StringComparison.Ordinal);
        }
    }

    internal static void SetCloaked(IntPtr window, bool cloaked)
    {
        var value = cloaked ? 1u : 0u;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(window, 13, ref value, sizeof(uint)));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind
    {
        internal uint Flags;
        internal int Enabled;
        internal IntPtr Region;
        internal int TransitionOnMaximized;
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr window, ref BlurBehind blur);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref uint value, int size);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProcedure(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam,
        UIntPtr subclassId, UIntPtr referenceData);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProcedure procedure, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProcedure procedure, UIntPtr id);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
