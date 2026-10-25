using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BrightnessTray.App.Startup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace BrightnessTray.App;

internal static class TrayMenuSmokeCheck
{
    internal static async Task RunAsync(FlyoutWindow window, StartupMenuItem startup, DemoStartupRegistration registration,
        DemoDisplayDiscovery discovery, BackdropCallbackErrorTracker errors, string directory)
    {
        var exits = 0;
        window.ConfigureTrayMenu(startup, () => { exits++; return Task.CompletedTask; });
        // Opening a menu must never block on display discovery.
        using var discoveryGate = new ManualResetEventSlim(false);
        discovery.DiscoveryGate = discoveryGate;
        var workArea = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var menuAnchor = new System.Drawing.Point(workArea.Left + workArea.Width / 2, workArea.Bottom);
        try { await window.ShowTrayMenuAsync(menuAnchor).WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { discovery.DiscoveryGate = null; discoveryGate.Set(); }
        Require(window.IsTrayMenuVisible && window.BackdropIsActive && window.Root.ActualWidth == 248 && discovery.DiscoveryCount == 0,
            "The menu did not open with the existing acrylic window.");
        Require(NativeWindowPosition.ClientFillsWindow(window.WindowHandle), "The menu has a native frame inset.");
        var menuBounds = NativeWindowPosition.GetBounds(window.WindowHandle);
        Require(Math.Abs((menuBounds.Left + menuBounds.Right) / 2.0 - menuAnchor.X) <= 1,
            "The tray icon anchor is not centered below the menu.");
        var buttons = new[] { "MenuOpenButton", "MenuRefreshButton", "MenuStartupButton", "MenuExitButton" }
            .Select(name => (Button)window.Root.FindName(name)).ToArray();
        Require(buttons.All(button => Math.Abs(button.ActualHeight - 28) < 0.1), "Menu highlights must be 28 DIP high.");
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            window.Root.RequestedTheme = theme;
            await Task.Delay(100);
            window.RequireGrainConfiguration();
            File.WriteAllText(Path.Combine(directory, $"acrylic-{theme.ToString().ToLowerInvariant()}.json"),
                JsonSerializer.Serialize(((FlyoutAcrylicBackdrop)window.PanelSystemBackdrop!).NativeRecipe,
                    new JsonSerializerOptions { WriteIndented = true }));
            Require(VisualStateManager.GoToState(buttons[3], "PointerOver", false), "The menu item has no native hover state.");
            window.PanelRoot.Background = new SolidColorBrush(theme == ElementTheme.Light
                ? global::Windows.UI.Color.FromArgb(255, 243, 243, 243) : global::Windows.UI.Color.FromArgb(255, 32, 32, 32));
            await CaptureAsync(window.Root, directory, $"tray-menu-{theme.ToString().ToLowerInvariant()}.png");
            window.PanelRoot.Background = null;
        }
        await TransparencySmokeCheck.RunAsync(window, directory);
        await window.InvokeTrayMenuCommandForTestAsync("Startup");
        Require(registration.Status.IsEnabled && !window.IsVisible, "Startup command did not toggle the existing registration.");
        registration.Status = new(true, false, "Managed by Windows policy.");
        await window.ShowTrayMenuAsync();
        Require(!buttons[2].IsEnabled && ((FontIcon)window.Root.FindName("MenuStartupCheck")).Visibility == Visibility.Visible,
            "The menu lost the startup checkmark or ignored Windows policy.");
        await window.InvokeTrayMenuCommandForTestAsync("Open");
        Require(!window.IsTrayMenuVisible && window.IsVisible && window.Root.ActualWidth == 360 && window.ViewModel.Displays.Count == 2,
            "Open brightness did not restore the brightness panel.");
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            window.Root.RequestedTheme = theme;
            await Task.Delay(100);
            window.PanelRoot.Background = new SolidColorBrush(theme == ElementTheme.Light
                ? global::Windows.UI.Color.FromArgb(255, 243, 243, 243) : global::Windows.UI.Color.FromArgb(255, 32, 32, 32));
            await CaptureAsync(window.Root, directory, $"brightness-compact-{theme.ToString().ToLowerInvariant()}.png");
            window.PanelRoot.Background = null;
        }
        await TransparencySmokeCheck.RunAsync(window, directory);
        discovery.InternalAvailable = false;
        await window.RefreshOrShowAsync();
        await Task.Delay(100);
        Require(window.ViewModel.Displays.Count == 1 && window.Root.ActualHeight < 220,
            "A single compact card still leaves space from the old minimum window height.");
        var singleDisplayHeight = window.Root.ActualHeight;
        window.Hide();
        await window.ShowNearTrayAsync();
        Require(Math.Abs(window.Root.ActualHeight - singleDisplayHeight) < 1,
            "Opening and refreshing a single-display panel use different heights.");
        window.PanelRoot.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 32, 32, 32));
        await CaptureAsync(window.Root, directory, "brightness-single-dark.png");
        window.PanelRoot.Background = null;
        discovery.InternalAvailable = true;
        await window.ShowTrayMenuAsync();
        var discoveriesBeforeRefresh = discovery.DiscoveryCount;
        await window.InvokeTrayMenuCommandForTestAsync("Refresh");
        Require(!window.IsTrayMenuVisible && !window.IsVisible && window.ViewModel.Displays.Count == 2 && discovery.DiscoveryCount > discoveriesBeforeRefresh,
            "Refresh displays did not refresh while keeping the panel hidden.");
        await window.ShowTrayMenuAsync();
        var menuClosed = 0;
        window.TrayMenuClosed += () => menuClosed++;
        window.DismissOnDeactivation(taskbarPointerDown: false);
        await Task.Delay(150);
        Require(!window.IsVisible && menuClosed == 1, "Light dismissal did not hide the menu and return focus to the tray.");
        await window.ShowTrayMenuAsync();
        await window.InvokeTrayMenuCommandForTestAsync("Exit");
        Require(exits == 1 && !window.IsVisible, "Exit did not hide the menu and invoke the application exit command once.");
        errors.ThrowIfAny();
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            success = true, menuWidth = 248, itemHeight = 28, nativeFrame = true, sharedAcrylicWindow = true,
            menuSkipsDisplayDiscovery = true, startupPolicyRespected = true, allFourCommands = true,
            lightDismissal = true, refreshKeepsPanelHidden = true, singleDisplayHeight,
            liveTransparencyPolicy = true, transparencyDisablesGrain = true,
            actualWindowsStartupChanged = false, backdropCallbackErrors = errors.Count,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task CaptureAsync(UIElement root, string directory, string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }
}
