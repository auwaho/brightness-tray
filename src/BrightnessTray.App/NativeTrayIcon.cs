using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Forms = System.Windows.Forms;

namespace BrightnessTray.App;

// Owning the shell identity avoids private NotifyIcon reflection and exposes its bounds for wheel hit testing.
internal sealed partial class NativeTrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8001;
    private const uint IconId = 1;
    private readonly MessageWindow _window;
    private readonly TrayMouseWheel _wheel;
    private readonly DispatcherQueueTimer _boundsTimer;
    private readonly uint _taskbarCreated;
    private readonly TrayIconHoverState _hover = new();
    private Icon? _icon;
    private string _text = string.Empty;
    private bool _visible;
    private bool _registered;
    private bool _version4;
    private bool _suppressSelect;
    private bool _disposed;
    private long _lastRegisterAttempt;

    internal NativeTrayIcon(DispatcherQueue dispatcher)
    {
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _window = new MessageWindow(this);
        try
        {
            _wheel = new TrayMouseWheel(dispatcher, delta =>
            {
                if (!_disposed && _visible && _registered) MouseWheel?.Invoke(delta);
            });
            _boundsTimer = dispatcher.CreateTimer();
            _boundsTimer.Interval = TimeSpan.FromMilliseconds(100);
            _boundsTimer.Tick += RefreshBounds;
            _boundsTimer.Start();
        }
        catch { _wheel?.Dispose(); _window.DestroyHandle(); throw; }
    }

    internal event Forms.MouseEventHandler? MouseDown;
    internal event Action<int>? MouseWheel;
    internal event Action? MouseHover;
    internal event Action? ContextMenuRequested;
    internal Func<bool>? IsContextMenuOpen { get; set; }
    internal Icon? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            if (value is null) RemoveIcon();
            else UpdateIcon();
        }
    }
    internal string Text
    {
        get => _text;
        set
        {
            var text = value ?? string.Empty;
            if (_text == text) return;
            _text = text;
            UpdateIcon();
        }
    }
    internal bool Visible
    {
        get => _visible;
        set
        {
            if (_disposed) return;
            _visible = value;
            if (value) UpdateIcon();
            else RemoveIcon();
            RefreshBounds(null, null);
        }
    }

    internal Rectangle? GetBounds()
    {
        if (_disposed || !_visible || !_registered) return null;
        var identifier = new IconIdentifier
        {
            Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = _window.Handle, Id = IconId, Guid = Guid.Empty,
        };
        // S_FALSE may return the overflow chevron: it is not our visible icon and must not capture scrolling.
        return Shell_NotifyIconGetRect(ref identifier, out var bounds) == 0
            && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top
            ? Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom) : null;
    }

    private void RefreshBounds(DispatcherQueueTimer? sender, object? args)
    {
        if (_disposed) return;
        if (_visible && !_registered && Environment.TickCount64 - _lastRegisterAttempt >= 1000) UpdateIcon();
        var root = IntPtr.Zero;
        var pointer = Forms.Cursor.Position;
        if (_visible && _registered && IsContextMenuOpen?.Invoke() != true && (MouseWheel is not null || MouseHover is not null))
        {
            root = GetAncestor(WindowFromPoint(pointer), 2);
            var name = new StringBuilder(96);
            GetClassName(root, name, name.Capacity);
            var shellClass = name.ToString();
            if (shellClass is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                && !shellClass.Contains("Overflow", StringComparison.Ordinal)) root = IntPtr.Zero;
        }
        // Avoid shell IPC while the pointer is elsewhere on the desktop.
        var bounds = root == IntPtr.Zero ? null : GetBounds();
        _wheel.UpdateRegion(MouseWheel is null ? null : bounds, root);
        UpdatePointerHover(bounds, pointer);
    }

    private void UpdatePointerHover(Rectangle? bounds, Point pointer)
    {
        if (_disposed || !_visible || !_registered || IsContextMenuOpen?.Invoke() == true) _hover.Reset();
        else if (_hover.Entered(bounds, pointer)) MouseHover?.Invoke();
    }

    private void UpdateIcon()
    {
        if (_disposed || !_visible || _icon is null) return;
        var data = CreateData();
        _lastRegisterAttempt = Environment.TickCount64;
        if (_registered && Shell_NotifyIcon(1, ref data)) return;
        ResetRegistration();
        _registered = Shell_NotifyIcon(0, ref data);
        if (_registered)
        {
            data.Version = 4;
            _version4 = Shell_NotifyIcon(4, ref data);
        }
    }

    private IconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<IconData>(), Window = _window.Handle, Id = IconId,
        Flags = 1u | 2u | 4u | 0x80u, Callback = CallbackMessage, Icon = _icon?.Handle ?? IntPtr.Zero,
        Tip = _text.Length > 127 ? _text[..127] : _text,
        Info = string.Empty, InfoTitle = string.Empty,
    };

    private void RemoveIcon()
    {
        var wasRegistered = _registered;
        ResetRegistration();
        if (!wasRegistered) return;
        var data = CreateData();
        Shell_NotifyIcon(2, ref data);
    }

    private void ResetRegistration()
    {
        _registered = false;
        _version4 = false;
        _suppressSelect = false;
        _hover.Reset();
        _wheel.UpdateRegion(null, IntPtr.Zero);
    }

    private void HandleMessage(ref Forms.Message message)
    {
        if (_disposed) return;
        if ((uint)message.Msg == _taskbarCreated)
        {
            ResetRegistration();
            UpdateIcon();
        }
        else if (message.Msg == CallbackMessage)
        {
            if (!_visible || !_registered) return;
            var notification = _version4 ? (int)(message.LParam.ToInt64() & 0xffff) : message.LParam.ToInt32();
            if (notification == 0x0200) RefreshBounds(null, null); // WM_MOUSEMOVE, including entry into an overflow flyout.
            if (notification is 0x0201 or 0x0203)
            {
                _suppressSelect = true;
                RaiseMouseDown(Forms.MouseButtons.Left);
            }
            else if (notification == 0x0204) RaiseMouseDown(Forms.MouseButtons.Right);
            else if (notification == 0x0400) // NIN_SELECT follows a normal mouse click.
            {
                if (!_suppressSelect) RaiseMouseDown(Forms.MouseButtons.Left);
                _suppressSelect = false;
            }
            else if (notification == 0x0401) // NIN_KEYSELECT: keyboard activation.
            {
                _suppressSelect = false;
                RaiseMouseDown(Forms.MouseButtons.Left);
            }
            else if (notification == 0x007B || (!_version4 && notification == 0x0205)) ShowMenu();
        }
    }

    private void RaiseMouseDown(Forms.MouseButtons button) => MouseDown?.Invoke(this,
        new Forms.MouseEventArgs(button, 1, Forms.Cursor.Position.X, Forms.Cursor.Position.Y, 0));

    private void ShowMenu()
    {
        if (IsContextMenuOpen?.Invoke() == true) return;
        _wheel.UpdateRegion(null, IntPtr.Zero);
        _hover.Reset();
        ContextMenuRequested?.Invoke();
    }

    internal void ReturnFocusAfterMenu()
    {
        if (_disposed || !_registered) return;
        var data = CreateData();
        Shell_NotifyIcon(3, ref data); // Return keyboard focus to the notification area.
        PostMessage(_window.Handle, 0, IntPtr.Zero, IntPtr.Zero);
        RefreshBounds(null, null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _boundsTimer.Stop();
        _boundsTimer.Tick -= RefreshBounds;
        RemoveIcon();
        _wheel.Dispose();
        _window.DestroyHandle();
    }

    private sealed class MessageWindow : Forms.NativeWindow
    {
        private readonly NativeTrayIcon _owner;
        internal MessageWindow(NativeTrayIcon owner)
        {
            _owner = owner;
            CreateHandle(new Forms.CreateParams { Caption = "BrightnessTray tray callbacks", ExStyle = 0x80 });
        }
        protected override void WndProc(ref Forms.Message message)
        {
            _owner.HandleMessage(ref message);
            base.WndProc(ref message);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconData
    {
        internal uint Size;
        internal IntPtr Window;
        internal uint Id;
        internal uint Flags;
        internal uint Callback;
        internal IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State;
        internal uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Guid;
        internal IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IconIdentifier
    {
        internal uint Size;
        internal IntPtr Window;
        internal uint Id;
        internal Guid Guid;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left; internal int Top; internal int Right; internal int Bottom; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref IconData data);
    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out NativeRect bounds);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
