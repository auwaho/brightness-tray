using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;

namespace BrightnessTray.App;

// Captures only owned windows so the compositor's actual Acrylic, not a bitmap fallback, is measured.
internal static class AcrylicNativeSmokeCheck
{
    internal static async Task RunAsync(FlyoutWindow window, BackdropCallbackErrorTracker errors, string directory)
    {
        if (!window.SystemTransparencyEnabled) throw new InvalidOperationException("Enable Windows Transparency effects for native Acrylic comparison.");
        var theme = window.Root.RequestedTheme;
        window.Root.RequestedTheme = ElementTheme.Dark;
        await Task.Delay(120);
        var backdrop = (FlyoutAcrylicBackdrop)window.PanelSystemBackdrop!;
        var tunedTint = backdrop.NativeTintOpacity;
        var bounds = NativeWindowPosition.GetBounds(window.WindowHandle);
        var rectangle = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        var backgroundBounds = Rectangle.Inflate(rectangle, 64, 64);
        await using var background = new UiSmokeCheck.FocusTargetWindow();
        try
        {
            await background.ShowAsync(directory, "--acrylic-background",
                backgroundBounds.Left.ToString(CultureInfo.InvariantCulture), backgroundBounds.Top.ToString(CultureInfo.InvariantCulture),
                backgroundBounds.Width.ToString(CultureInfo.InvariantCulture), backgroundBounds.Height.ToString(CultureInfo.InvariantCulture));
            if (!SetWindowPos(window.WindowHandle, new IntPtr(-1), 0, 0, 0, 0, 0x0013))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            await Task.Delay(300);
            var host = (Grid)window.Root.FindName("GrainHost");
            if (host.Background is not SolidColorBrush density || Math.Abs(density.Opacity - 0.20) > 0.00001)
                throw new InvalidOperationException("The user-selected 20% overlay was not preserved.");

            var baselineBackdrop = new BaselineAcrylicBackdrop();
            window.PanelSystemBackdrop = baselineBackdrop;
            await Task.Delay(400);
            window.EnableGrainForNativeProbe();
            await Task.Delay(80);
            RequireVisible(window, baselineBackdrop.IsActive);
            var baselineTint = baselineBackdrop.TintOpacity;
            if (Math.Abs(baselineTint - 0.15f) > 0.0001f)
                throw new InvalidOperationException("The connected baseline did not retain its historical tint opacity.");
            using var before = NativeTransparencyCheck.CaptureOwnedArea(rectangle, window.WindowHandle, background.Handle);
            before.Save(Path.Combine(directory, "acrylic-before.png"), ImageFormat.Png);
            var baseline = Measure(before, window.Root.XamlRoot.RasterizationScale);

            window.PanelSystemBackdrop = backdrop;
            await Task.Delay(400);
            RequireVisible(window, backdrop.IsMaterialActive);
            window.RequireNativeRecipe();
            using var after = NativeTransparencyCheck.CaptureOwnedArea(rectangle, window.WindowHandle, background.Handle);
            after.Save(Path.Combine(directory, "acrylic-after.png"), ImageFormat.Png);
            var tuned = Measure(after, window.Root.XamlRoot.RasterizationScale);
            errors.ThrowIfAny();
            var comparison = JsonSerializer.Serialize(new
            {
                success = tuned.Spread < baseline.Spread * 0.85, baselineTint, tunedTint, overlayOpacity = 0.20,
                background = "#6A8416", baseline, tuned,
                chromaReduction = 1 - tuned.Spread / baseline.Spread,
                lumaChange = tuned.Luma - baseline.Luma,
                recipe = backdrop.NativeRecipe, backdropCallbackErrors = errors.Count,
                hardwareWrites = false, windowsSettingsChanged = false,
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(directory, "comparison.json"), comparison);
            if (tuned.Spread >= baseline.Spread * 0.85)
                throw new InvalidOperationException($"Native Acrylic did not reduce chroma: {baseline.Spread:F2} -> {tuned.Spread:F2}.");
            File.WriteAllText(Path.Combine(directory, "result.json"), comparison);
        }
        finally
        {
            window.PanelSystemBackdrop = backdrop;
            window.Root.RequestedTheme = theme;
        }
    }

    private static void RequireVisible(FlyoutWindow window, bool materialActive)
    {
        if (!window.IsVisible || !materialActive)
            throw new InvalidOperationException("The native Acrylic test requires an unobstructed active panel.");
    }

    private static Sample Measure(Bitmap bitmap, double scale)
    {
        double r = 0, g = 0, b = 0;
        var count = 0;
        for (var y = (int)(40 * scale); y < (int)(100 * scale); y++)
        for (var x = (int)(8 * scale); x < (int)(16 * scale); x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            r += pixel.R; g += pixel.G; b += pixel.B; count++;
        }
        r /= count; g /= count; b /= count;
        return new(r, g, b, Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)), 0.2126 * r + 0.7152 * g + 0.0722 * b);
    }

    private sealed record Sample(double R, double G, double B, double Spread, double Luma);

    // Historical connected recipe, applied before attaching the native target like the production recipe.
    private sealed class BaselineAcrylicBackdrop : SystemBackdrop
    {
        private DesktopAcrylicController? _controller;
        internal bool IsActive => _controller?.State == SystemBackdropState.Active;
        internal float TintOpacity => _controller!.TintOpacity;

        protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
        {
            base.OnTargetConnected(target, root);
            _controller = new DesktopAcrylicController();
            _controller.SetSystemBackdropConfiguration(new SystemBackdropConfiguration { Theme = SystemBackdropTheme.Dark, IsInputActive = true });
            _controller.TintColor = global::Windows.UI.Color.FromArgb(255, 44, 44, 44);
            _controller.TintOpacity = 0.15f;
            _controller.LuminosityOpacity = 0.96f;
            _controller.FallbackColor = global::Windows.UI.Color.FromArgb(255, 44, 44, 44);
            _controller.AddSystemBackdropTarget(target);
        }

        protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root) { }

        protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
        {
            var controller = _controller;
            _controller = null;
            try { base.OnTargetDisconnected(target); }
            finally
            {
                try { controller?.RemoveSystemBackdropTarget(target); }
                finally { controller?.Dispose(); }
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
