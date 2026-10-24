using System;
using System.Linq;
using System.Threading.Tasks;
using BrightnessTray.App.ViewModels;
using BrightnessTray.Core;
using Microsoft.UI.Dispatching;

namespace BrightnessTray.App;

internal static class TrayBrightnessToolTipSmokeCheck
{
    internal static async Task RunAsync(FlyoutWindow window)
    {
        var discovery = new DemoDisplayDiscovery();
        await using var service = new BrightnessService(discovery);
        await using var flyout = new FlyoutViewModel(service);
        await using var wheel = new TrayBrightnessController(flyout);
        using var icon = TrayIconImage.Create();
        using var tray = new NativeTrayIcon(DispatcherQueue.GetForCurrentThread())
        {
            Icon = icon, Text = "BrightnessTray", Visible = true,
        };
        await using var tooltip = new TrayBrightnessToolTip(flyout, text => tray.Text = text);
        var foreground = NativeWindowPosition.ForegroundWindow;
        var position = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        var visible = window.IsVisible;
        await tooltip.RefreshOnHoverAsync();
        Require(tray.Text.Contains("DELL U2723QE: 65%") && tray.Text.Contains("Laptop display: 80%"),
            "The native hover tooltip did not show the current brightness of both displays.");
        tray.RequireNativeToolTipForTest(tray.Text);
        await wheel.ScrollAsync(120);
        Require(tray.Text.Contains("70%") && tray.Text.Contains("85%"),
            "The native tooltip did not immediately follow the fixed wheel step.");
        await DrainAsync(flyout);
        Require(tray.Text.Contains("70%") && tray.Text.Contains("85%"),
            "The native tooltip did not preserve confirmed brightness.");
        flyout.Displays[0].Brightness = 45;
        await DrainAsync(flyout);
        Require(tray.Text.Contains("DELL U2723QE: 45%"), "The native tooltip did not follow the slider.");
        discovery.InternalAvailable = false;
        await flyout.RefreshAsync();
        Require(!tray.Text.Contains("Laptop display") && tray.Text.Contains("45%"),
            "An unavailable display remained in the native tooltip.");
        discovery.InternalAvailable = true;
        await flyout.RefreshAsync();
        Require(tray.Text.Contains("Laptop display: 85%"), "A recovered display did not return to the native tooltip.");
        Require(NativeWindowPosition.ForegroundWindow == foreground && window.IsVisible == visible
            && window.AppWindow.Position == position && window.AppWindow.Size == size,
            "Updating the native tooltip changed focus or the main flyout.");
        await tooltip.DisposeAsync();
        var disposedText = tray.Text;
        flyout.Displays[0].Brightness = 50;
        await DrainAsync(flyout);
        Require(tray.Text == disposedText, "A disposed native tooltip retained model subscriptions.");
    }

    private static async Task DrainAsync(FlyoutViewModel flyout)
    {
        var rows = flyout.Displays.ToArray();
        await Task.WhenAll(rows.Select(row => row.PauseAndDrainAsync()));
        foreach (var row in rows) row.Resume();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
