using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BrightnessTray.App;

internal static class TrayIconImage
{
    internal static Color GetSystemForeground()
    {
        if (Forms.SystemInformation.HighContrast) return SystemColors.WindowText;
        try
        {
            // Windows mode controls the taskbar; app mode can be different in a custom theme.
            var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0) is int value && value != 0;
            return light ? Color.FromArgb(26, 26, 26) : Color.White;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Color.White;
        }
    }

    public static Icon Create() => Create(GetSystemForeground());

    public static Icon Create(Color foreground)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(foreground, 2.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawEllipse(pen, 8.5f, 8.5f, 15, 15);
        for (var index = 0; index < 8; index++)
        {
            var angle = index * Math.PI / 4;
            graphics.DrawLine(pen, 16 + (float)Math.Cos(angle) * 12.5f, 16 + (float)Math.Sin(angle) * 12.5f,
                16 + (float)Math.Cos(angle) * 14.5f, 16 + (float)Math.Sin(angle) * 14.5f);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
