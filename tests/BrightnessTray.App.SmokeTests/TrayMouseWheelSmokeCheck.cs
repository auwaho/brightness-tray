using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Forms = System.Windows.Forms;

namespace BrightnessTray.App;

internal static class TrayMouseWheelSmokeCheck
{
    internal static async Task RunAsync(DispatcherQueue dispatcher)
    {
        var bounds = new Rectangle(-1400, -40, 24, 24);
        Require(TrayMouseWheel.IsHit(bounds, 100, 100, -1390, -30, 120), "A visible negative-coordinate icon rejected scroll up.");
        Require(TrayMouseWheel.IsHit(bounds, 100, 350, -1390, -30, -15), "A high-resolution downward delta was rejected.");
        Require(!TrayMouseWheel.IsHit(bounds, 100, 351, -1390, -30, 120), "Stale icon bounds captured scrolling.");
        Require(!TrayMouseWheel.IsHit(bounds, 100, 99, -1390, -30, 120), "Invalid snapshot time captured scrolling.");
        Require(!TrayMouseWheel.IsHit(bounds, 100, 100, -1376, -30, 120), "The right edge captured a neighbouring icon.");
        Require(!TrayMouseWheel.IsHit(bounds, 100, 100, -1390, -16, 120), "The bottom edge captured unrelated input.");
        Require(!TrayMouseWheel.IsHit(bounds, 100, 100, 1, 1, 120), "Scrolling outside the tray icon was captured.");
        Require(!TrayMouseWheel.IsHit(bounds, 100, 100, -1390, -30, 0), "A zero delta was captured.");
        Require(!TrayMouseWheel.IsHit(Rectangle.Empty, 100, 100, 0, 0, 120), "An empty icon rectangle captured scrolling.");

        using var icon = TrayIconImage.Create();
        using var menu = new Forms.ContextMenuStrip();
        using var tray = new NativeTrayIcon(dispatcher)
        {
            Icon = icon,
            Text = "BrightnessTray native tray input test",
            ContextMenuStrip = menu,
            Visible = true,
        };
        var clicks = 0;
        var wheelCallbacks = 0;
        tray.MouseDown += (_, args) => { if (args.Button == Forms.MouseButtons.Left) clicks++; };
        tray.MouseWheel += _ => wheelCallbacks++;
        await Task.Delay(100);
        tray.RequireRegisteredForTest();
        tray.Text = "Panel A: 45%\nPanel B: 85%";
        tray.RequireNativeToolTipForTest("Panel A: 45%\nPanel B: 85%");
        var hoverCount = 0;
        tray.MouseHover += () => hoverCount++;
        tray.ReplayHoverForTest(true);
        tray.ReplayHoverForTest(true);
        tray.ReplayHoverForTest(false);
        tray.ReplayHoverForTest(true);
        Require(hoverCount == 2, "Hover refresh did not run once per icon entry.");
        tray.ReplayHoverForTest(false);
        tray.SendNotificationForTest(0x0201);
        tray.SendNotificationForTest(0x0400);
        Require(clicks == 1, "A mouse click plus NIN_SELECT toggled twice.");
        tray.SendNotificationForTest(0x0401);
        Require(clicks == 2, "Keyboard activation was lost.");
        Require(ReferenceEquals(tray.ContextMenuStrip, menu), "The tray menu was replaced.");
        tray.SendNotificationForTest(0x0201); // Recreate before the matching NIN_SELECT arrives.
        Require(clicks == 3, "The click before recreation was lost.");
        tray.RestartOwnRegistrationForTest();
        tray.RequireRegisteredForTest();
        tray.SendNotificationForTest(0x0400);
        Require(clicks == 4, "Stale click suppression swallowed standalone NIN_SELECT after recreation.");
        tray.Icon = null;
        tray.SendNotificationForTest(0x0201);
        tray.SendNotificationForTest(0x0400);
        tray.SendNotificationForTest(0x0401);
        Require(clicks == 4, "An unregistered icon handled a stale activation callback.");
        tray.RequireEmptyRegionForTest();
        tray.Icon = icon;
        tray.RequireRegisteredForTest();
        tray.Visible = false;
        var previousHoverCount = hoverCount;
        tray.ReplayHoverForTest(true);
        Require(hoverCount == previousHoverCount, "A hidden icon refreshed its tooltip.");
        tray.SendNotificationForTest(0x0201);
        tray.SendNotificationForTest(0x0400);
        tray.SendNotificationForTest(0x0401);
        Require(clicks == 4, "A hidden icon handled a stale activation callback.");
        Require(tray.GetBounds() is null, "A hidden icon retained wheel bounds.");
        tray.RequireEmptyRegionForTest();
        Require(wheelCallbacks == 0, "Tray lifecycle generated an unsolicited brightness edit.");
        tray.Dispose();
        tray.Dispose();
        tray.DispatchNotificationForTest(0x0201);
        tray.DispatchNotificationForTest(0x0401);
        Require(clicks == 4, "A disposed icon handled a stale activation callback.");
        tray.RequireDisposedForTest();
    }

    private static void Require(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }
}

internal sealed partial class NativeTrayIcon
{
    internal void RequireNativeToolTipForTest(string expected)
    {
        var data = CreateData();
        if ((data.Flags & 0x84) != 0x84 || data.Tip != expected || data.Tip.Length > 127)
            throw new InvalidOperationException("The standard native tooltip did not receive the display brightness text.");
    }

    internal void ReplayHoverForTest(bool inside) => UpdatePointerHover(
        inside ? new Rectangle(0, 0, 24, 24) : null, inside ? Point.Empty : new Point(24, 24));

    internal void RequireRegisteredForTest()
    {
        if (!_registered || _window.Handle == IntPtr.Zero)
            throw new InvalidOperationException("The own native tray icon was not registered.");
        _wheel.RequireInstalledForTest();
        // An overflow icon can return S_FALSE while closed; a nonempty S_OK rectangle is validated when available.
        var bounds = GetBounds();
        if (bounds is { Width: <= 0 } or { Height: <= 0 })
            throw new InvalidOperationException("The shell returned an invalid own-icon rectangle.");
    }

    internal void SendNotificationForTest(int notification) => SendMessageForTest(_window.Handle, CallbackMessage,
        IntPtr.Zero, new IntPtr(_version4 ? notification | (1 << 16) : notification));

    internal void DispatchNotificationForTest(int notification)
    {
        // The HWND no longer exists after disposal, so exercise late callback rejection directly.
        var message = Forms.Message.Create(_window.Handle, CallbackMessage, IntPtr.Zero, new IntPtr(notification));
        HandleMessage(ref message);
    }

    internal void RestartOwnRegistrationForTest()
    {
        RemoveIcon();
        SendMessageForTest(_window.Handle, (int)_taskbarCreated, IntPtr.Zero, IntPtr.Zero);
    }

    internal void RequireEmptyRegionForTest() => _wheel.RequireEmptyRegionForTest();
    internal void RequireDisposedForTest()
    {
        if (!_disposed || _registered || _window.Handle != IntPtr.Zero)
            throw new InvalidOperationException("The native tray owner window survived disposal.");
        _wheel.RequireStoppedForTest();
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessageForTest(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}

internal sealed partial class TrayMouseWheel
{
    internal void RequireInstalledForTest()
    {
        if (_hook == IntPtr.Zero || !_thread.IsAlive)
            throw new InvalidOperationException("The dedicated tray wheel hook is not installed.");
    }
    internal void RequireEmptyRegionForTest()
    {
        if (_region is not null) throw new InvalidOperationException("A hidden tray icon retained an active hit region.");
    }
    internal void RequireStoppedForTest()
    {
        if (_hook != IntPtr.Zero || _thread.IsAlive || _region is not null)
            throw new InvalidOperationException("The tray hook survived disposal.");
    }
}
