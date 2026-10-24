using System;
using System.Runtime.InteropServices;

namespace BrightnessTray.App;

internal static partial class NativeWindowPosition
{
    internal static NativeRect GetBounds(IntPtr window)
    {
        if (!GetWindowRect(window, out var bounds))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return bounds;
    }

    internal static bool ClientFillsWindow(IntPtr window)
    {
        var bounds = GetBounds(window);
        var client = GetClientBounds(window);
        return client.Left == bounds.Left && client.Top == bounds.Top
            && client.Right == bounds.Right && client.Bottom == bounds.Bottom;
    }

    internal static string DescribeFrame(IntPtr window)
    {
        var bounds = GetBounds(window);
        var client = GetClientBounds(window);
        return $"Window={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}; "
            + $"Client={client.Left},{client.Top},{client.Right},{client.Bottom}; "
            + $"Style={GetStyle(window, -16):X8}; ExtendedStyle={GetStyle(window, -20):X8}";
    }

    private static NativeRect GetClientBounds(IntPtr window)
    {
        var origin = new NativePoint();
        if (!GetClientRect(window, out var client) || !ClientToScreen(window, ref origin))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new NativeRect { Left = origin.X, Top = origin.Y, Right = origin.X + client.Right, Bottom = origin.Y + client.Bottom };
    }

    private static long GetStyle(IntPtr window, int index)
    {
        var value = GetWindowLongPtr(window, index);
        var error = Marshal.GetLastWin32Error();
        if (value == IntPtr.Zero && error != 0) throw new System.ComponentModel.Win32Exception(error);
        return value.ToInt64();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
}
