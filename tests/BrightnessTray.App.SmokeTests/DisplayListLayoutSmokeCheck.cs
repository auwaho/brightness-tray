using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace BrightnessTray.App;

internal static class DisplayListLayoutSmokeCheck
{
    internal static async Task RunAsync(FlyoutWindow window, DemoDisplayDiscovery discovery, string directory)
    {
        var scroller = (ScrollViewer)window.Root.FindName("DisplayScroller");
        var footer = (Border)window.Root.FindName("FooterPanel");
        var backdrop = (FlyoutAcrylicBackdrop)window.PanelSystemBackdrop!;
        var originalCount = discovery.DisplayCount;
        var originalTheme = window.Root.RequestedTheme;
        var originalTransparency = backdrop.TransparencyEnabled;
        var originalOffset = scroller.VerticalOffset;
        var originalBounds = new RectInt32(window.AppWindow.Position.X, window.AppWindow.Position.Y,
            window.AppWindow.Size.Width, window.AppWindow.Size.Height);
        var observations = new List<object>();
        try
        {
            discovery.DisplayCount = 4;
            backdrop.SetTransparencyEnabled(false);
            await window.RefreshOrShowAsync();
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                window.Hide();
                window.Root.RequestedTheme = theme;
                await window.ShowNearTrayAsync();
                await ScrollAsync(scroller, 0);
                var scale = window.Root.XamlRoot.RasterizationScale;
                var area = System.Windows.Forms.Screen.FromHandle(window.WindowHandle).WorkingArea;
                var naturalHeight = scroller.ExtentHeight + scroller.Margin.Top + scroller.Margin.Bottom
                    + scroller.Padding.Top + scroller.Padding.Bottom + footer.ActualHeight;
                var fitsWorkArea = area.Height >= Math.Ceiling(naturalHeight * scale) + 2 * Math.Ceiling(12 * scale);
                RequireLayout(window, footer);
                if (fitsWorkArea)
                {
                    Require(scroller.ScrollableHeight <= 1 && window.DisplayRowsFit,
                        "Four displays still require scrolling despite sufficient work-area height.");
                    var viewport = Bounds(scroller, window.Root);
                    Require(Cards(window).All(card => Bounds(card, window.Root).Top >= viewport.Top - 1
                        && Bounds(card, window.Root).Bottom <= viewport.Bottom + 1),
                        "The full-height viewport clips a display card.");
                }
                var suffix = theme.ToString().ToLowerInvariant();
                await TrayMenuSmokeCheck.CaptureAsync(window.Root, directory, $"display-list-four-{suffix}.png");
                observations.Add(Snapshot(window, scroller, theme, "normal", fitsWorkArea, naturalHeight));

                window.AppWindow.Resize(new SizeInt32(window.AppWindow.Size.Width, (int)Math.Ceiling(300 * scale)));
                await ScrollAsync(scroller, 0);
                RequireLayout(window, footer);
                Require(scroller.ScrollableHeight > 1, "The constrained four-display viewport cannot scroll.");
                RequireScrollbarGutter(window, scroller);
                await TrayMenuSmokeCheck.CaptureAsync(window.Root, directory, $"display-list-four-{suffix}-short.png");
                observations.Add(Snapshot(window, scroller, theme, "short-top", fitsWorkArea, naturalHeight));

                await ScrollAsync(scroller, scroller.ScrollableHeight);
                RequireLayout(window, footer);
                Require(Math.Abs(scroller.VerticalOffset - scroller.ScrollableHeight) <= 1,
                    "The constrained display list did not reach its bottom.");
                RequireScrollbarGutter(window, scroller);
                await TrayMenuSmokeCheck.CaptureAsync(window.Root, directory, $"display-list-four-{suffix}-short-bottom.png");
                observations.Add(Snapshot(window, scroller, theme, "short-bottom", fitsWorkArea, naturalHeight));
            }

            File.WriteAllText(Path.Combine(directory, "display-list-layout.json"), JsonSerializer.Serialize(new
            {
                success = true, displayCount = 4, hardwareWrites = false, windowsSettingsChanged = false, observations,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            window.Hide();
            discovery.DisplayCount = originalCount;
            window.Root.RequestedTheme = originalTheme;
            backdrop.SetTransparencyEnabled(originalTransparency);
            await window.ShowNearTrayAsync();
            window.AppWindow.MoveAndResize(originalBounds);
            await ScrollAsync(scroller, originalOffset);
        }
    }

    private static void RequireLayout(FlyoutWindow window, Border footer)
    {
        Require(window.ViewModel.Displays.Count == 4 && window.ViewModel.Displays.All(row => row.IsAvailable),
            "The four-display preview has missing or inactive rows.");
        var scale = window.Root.XamlRoot.RasterizationScale;
        Require(Math.Abs(window.Root.ActualWidth - 360) <= 1 && Math.Abs(window.AppWindow.Size.Width / scale - 360) <= 1,
            "The display panel no longer measures 360 DIP wide.");
        Require(Math.Abs(footer.ActualHeight - 50) <= 1 && window.FooterFits,
            "The fixed 50 DIP footer is clipped or changed height.");
        var cards = Cards(window);
        Require(cards.Length == 4 && cards.All(card => Math.Abs(card.ActualWidth - 312) <= 1),
            "The scrollbar gutter changed the original 312 DIP card width.");
    }

    private static void RequireScrollbarGutter(FlyoutWindow window, ScrollViewer scroller)
    {
        var bars = Descendants(scroller).OfType<ScrollBar>().Where(bar => bar.Orientation == Orientation.Vertical
            && bar.Visibility == Visibility.Visible && bar.ActualWidth > 0 && bar.ActualHeight > 0).ToArray();
        Require(bars.Length > 0, "The constrained list has no arranged vertical scrollbar.");
        foreach (var bar in bars)
        foreach (var card in Cards(window))
        {
            var scrollbar = Bounds(bar, window.Root);
            var display = Bounds(card, window.Root);
            Require(scrollbar.Right <= display.Left + 0.5 || scrollbar.Left >= display.Right - 0.5
                || scrollbar.Bottom <= display.Top + 0.5 || scrollbar.Top >= display.Bottom - 0.5,
                $"The vertical scrollbar overlaps a display card: scrollbar={scrollbar}, card={display}.");
        }
    }

    private static object Snapshot(FlyoutWindow window, ScrollViewer scroller, ElementTheme theme, string state,
        bool fitsWorkArea, double naturalHeight) => new
    {
        theme = theme.ToString(), state, fitsWorkArea, naturalHeight,
        width = window.Root.ActualWidth, height = window.Root.ActualHeight,
        viewportHeight = scroller.ViewportHeight, extentHeight = scroller.ExtentHeight,
        scrollableHeight = scroller.ScrollableHeight, offset = scroller.VerticalOffset,
        footerFits = window.FooterFits,
        cards = Cards(window).Select(card => Describe(Bounds(card, window.Root))).ToArray(),
        scrollbars = Descendants(scroller).OfType<ScrollBar>().Where(bar => bar.Orientation == Orientation.Vertical)
            .Select(bar => Describe(Bounds(bar, window.Root))).ToArray(),
    };

    private static object Describe(Rect bounds) => new { bounds.X, bounds.Y, bounds.Width, bounds.Height };
    private static Rect Bounds(FrameworkElement element, UIElement root) => element.TransformToVisual(root)
        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static Border[] Cards(FlyoutWindow window) => Descendants(window.Root).OfType<Border>()
        .Where(border => border.Name == "DisplayCard").ToArray();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static async Task ScrollAsync(ScrollViewer scroller, double offset)
    {
        await Task.Delay(100);
        scroller.ChangeView(null, offset, null, true);
        await Task.Delay(100);
        scroller.UpdateLayout();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
