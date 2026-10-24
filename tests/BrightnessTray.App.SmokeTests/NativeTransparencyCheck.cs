using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace BrightnessTray.App;

// The XAML bitmap renderer cannot detect an opaque HWND or capture compositor shadows.
// This opt-in smoke check captures only our flyout over our own opaque, known-color window.
internal static class NativeTransparencyCheck
{
    internal static async Task RunAsync(FlyoutWindow window, string directory)
    {
        var bounds = NativeWindowPosition.GetBounds(window.WindowHandle);
        var margin = (int)Math.Ceiling(12 * window.Root.XamlRoot.RasterizationScale);
        var rectangle = Rectangle.Inflate(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), margin, margin);
        if (!Forms.Screen.FromHandle(window.WindowHandle).Bounds.Contains(rectangle))
            throw new InvalidOperationException("The transparency check requires the complete flyout on screen.");
        var backgroundColor = Color.FromArgb(80, 180, 220);
        using var background = new TestBackground
        {
            BackColor = backgroundColor,
            FormBorderStyle = Forms.FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = Forms.FormStartPosition.Manual,
            Bounds = Rectangle.FromLTRB(rectangle.Left - 32, rectangle.Top - 32, rectangle.Right + 32, rectangle.Bottom),
            TopMost = true,
        };
        background.Show();
        var workArea = Forms.Screen.FromHandle(window.WindowHandle).WorkingArea;
        NativeWindowPosition.KeepBelowTaskbar(background.Handle, bounds.Left + (bounds.Right - bounds.Left) / 2, workArea.Bottom + 1);
        // Creating the native test surface may deactivate (and dismiss) the light-dismiss flyout.
        window.Hide();
        var opening = window.ShowNearTrayAsync();
        if (!SetWindowPos(window.WindowHandle, new IntPtr(-1), 0, 0, 0, 0, 0x0013))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (window.AnimationsEnabled)
        {
            var capturedOpening = false;
            for (var attempt = 0; attempt < 150 && !opening.IsCompleted; attempt++)
            {
                var distance = window.RevealDistance;
                var remaining = (window.AppWindow.Position.Y - bounds.Top) / window.Root.XamlRoot.RasterizationScale;
                if (window.IsVisible && distance > 0 && remaining > distance * 0.35 && remaining < distance * 0.65)
                {
                    using var partial = CaptureOwnedArea(rectangle, window.WindowHandle, background.Handle);
                    partial.Save(Path.Combine(directory, "native-opening.png"), ImageFormat.Png);
                    var top = partial.GetPixel(rectangle.Width / 2, rectangle.Height / 8);
                    // Sample the panel's side padding, where accent-colored sliders cannot mimic the test background.
                    var bottom = partial.GetPixel(margin + (int)Math.Ceiling(8 * window.Root.XamlRoot.RasterizationScale), rectangle.Height * 7 / 8);
                    if (!IsNearBackground(top, backgroundColor, 35) || IsNearBackground(bottom, backgroundColor, 35))
                        throw new InvalidOperationException("Opening did not reveal the panel from the bottom with a transparent area above it.");
                    capturedOpening = true;
                    break;
                }
                await Task.Delay(5);
            }
            if (!capturedOpening)
                throw new InvalidOperationException("Could not observe the panel during its full-height slide.");
        }
        await opening;
        await Task.Delay(150);
        var centerOwner = GetAncestor(WindowFromPoint(new Point(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2)), 2);
        if (!window.IsVisible || centerOwner != window.WindowHandle)
            throw new InvalidOperationException($"The flyout is obscured during the native check: visible={window.IsVisible}, center={centerOwner}, flyout={window.WindowHandle}, background={background.Handle}.");
        using var capture = CaptureOwnedArea(rectangle, window.WindowHandle, background.Handle);
        capture.Save(Path.Combine(directory, "native-transparency.png"), ImageFormat.Png);
        var centerColor = capture.GetPixel(rectangle.Width / 2, rectangle.Height / 2);
        if (Math.Abs(centerColor.R - backgroundColor.R) < 5 && Math.Abs(centerColor.G - backgroundColor.G) < 5
            && Math.Abs(centerColor.B - backgroundColor.B) < 5)
            throw new InvalidOperationException("The native check did not capture a visible flyout surface.");
        var inset = Math.Max(1, (int)Math.Round(2 * window.Root.XamlRoot.RasterizationScale));
        foreach (var sample in new[] { new Point(inset, inset), new Point(rectangle.Width / 2, inset),
            new Point(inset, rectangle.Height / 2), new Point(rectangle.Width - inset - 1, rectangle.Height / 2) })
        {
            var color = capture.GetPixel(sample.X, sample.Y);
            if (Math.Abs(color.R - backgroundColor.R) > 35 || Math.Abs(color.G - backgroundColor.G) > 35
                || Math.Abs(color.B - backgroundColor.B) > 35)
                throw new InvalidOperationException($"The shadow margin is opaque or excessively dark at {sample}: {color}.");
        }
        var cornerSize = (int)Math.Ceiling(12 * window.Root.XamlRoot.RasterizationScale);
        var panelColor = capture.GetPixel(margin + cornerSize, margin + cornerSize);
        var antialiasedPixels = 0;
        for (var y = margin; y < margin + cornerSize; y++)
        for (var x = margin; x < margin + cornerSize; x++)
        {
            var pixel = capture.GetPixel(x, y);
            if (!IsNearBackground(pixel, backgroundColor, 10) && !IsNearBackground(pixel, panelColor, 20))
                antialiasedPixels++;
        }
        if (antialiasedPixels < 4)
            throw new InvalidOperationException("The window corner has no antialiased transition to the background.");
        var shadowColor = capture.GetPixel(margin / 2, rectangle.Height / 2);
        if (backgroundColor.B - shadowColor.B < 5)
            throw new InvalidOperationException("The native window shadow is barely visible.");
        using var icon = TrayIconImage.Create();
        using var iconBitmap = icon.ToBitmap();
        var hasVisiblePixel = false;
        var left = iconBitmap.Width;
        var right = -1;
        for (var y = 0; y < iconBitmap.Height; y++)
        for (var x = 0; x < iconBitmap.Width; x++)
        {
            var pixel = iconBitmap.GetPixel(x, y);
            if (pixel.A == 0) continue;
            hasVisiblePixel = true;
            if (pixel.A >= 128) { left = Math.Min(left, x); right = Math.Max(right, x); }
            // HICON conversion can leave antialiased edge pixels in premultiplied-alpha form.
            if (pixel.R != pixel.G || pixel.G != pixel.B || pixel.R < pixel.A - 2)
                throw new InvalidOperationException("The brightness tray icon is not white.");
        }
        if (!hasVisiblePixel || iconBitmap.GetPixel(iconBitmap.Width / 2, iconBitmap.Height / 2).A != 0)
            throw new InvalidOperationException("The brightness tray icon does not have a hollow center.");
        if (right - left + 1 < 29)
            throw new InvalidOperationException("The brightness tray icon does not fill its available slot.");
        iconBitmap.Save(Path.Combine(directory, "tray-icon.png"), ImageFormat.Png);
    }

    private static bool IsNearBackground(Color sample, Color background, int tolerance) =>
        Math.Abs(sample.R - background.R) <= tolerance && Math.Abs(sample.G - background.G) <= tolerance
        && Math.Abs(sample.B - background.B) <= tolerance;

    private static Bitmap CaptureOwnedArea(Rectangle rectangle, IntPtr flyout, IntPtr background)
    {
        // Refuse to capture if another window covers even part of these owned surfaces.
        for (var y = 0; y < rectangle.Height; y += 4)
        for (var x = 0; x < rectangle.Width; x += 4)
        {
            var owner = GetAncestor(WindowFromPoint(new Point(rectangle.Left + x, rectangle.Top + y)), 2);
            if (owner != flyout && owner != background)
                throw new InvalidOperationException("Another window obscures the owned transparency test area.");
        }
        var capture = new Bitmap(rectangle.Width, rectangle.Height);
        try
        {
            using var graphics = Graphics.FromImage(capture);
            graphics.CopyFromScreen(rectangle.Location, Point.Empty, rectangle.Size);
            return capture;
        }
        catch { capture.Dispose(); throw; }
    }

    private sealed class TestBackground : Forms.Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
