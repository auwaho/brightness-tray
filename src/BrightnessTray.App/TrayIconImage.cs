using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace BrightnessTray.App;

internal static class TrayIconImage
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.White, 2.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
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
