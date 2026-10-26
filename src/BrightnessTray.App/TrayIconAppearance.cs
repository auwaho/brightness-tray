using System;
using System.Drawing;

namespace BrightnessTray.App;

internal sealed class TrayIconAppearance : IDisposable
{
    private readonly NativeTrayIcon _tray;
    private readonly Func<Color> _readForeground;
    private Icon? _icon;
    private Color? _foreground;
    private bool _disposed;

    internal TrayIconAppearance(NativeTrayIcon tray, Func<Color>? readForeground = null)
    {
        _tray = tray;
        _readForeground = readForeground ?? TrayIconImage.GetSystemForeground;
        _tray.AppearanceChanged += Refresh;
        try { Refresh(); }
        catch { Dispose(); throw; }
    }

    private void Refresh()
    {
        if (_disposed) return;
        var foreground = Color.FromArgb(_readForeground().ToArgb());
        if (_foreground == foreground) return;
        var next = TrayIconImage.Create(foreground);
        var previous = _icon;
        _icon = next;
        _foreground = foreground;
        // Shell must receive the new HICON before its predecessor is released.
        _tray.Icon = next;
        previous?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tray.AppearanceChanged -= Refresh;
        _tray.Icon = null;
        _icon?.Dispose();
        _icon = null;
    }
}
