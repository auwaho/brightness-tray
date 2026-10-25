using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace BrightnessTray.App;

internal static class TransparencySmokeCheck
{
    internal static async Task RunAsync(FlyoutWindow window, string directory)
    {
        var backdrop = (FlyoutAcrylicBackdrop)window.PanelSystemBackdrop!;
        var handle = window.WindowHandle;
        var menu = window.IsTrayMenuVisible;
        var theme = window.Root.RequestedTheme;
        try
        {
            foreach (var appearance in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                window.Root.RequestedTheme = appearance;
                backdrop.SetTransparencyEnabled(true);
                await Task.Delay(80);
                window.RequireNativeRecipe();
                var grainEnabled = backdrop.IsGrainEnabled;
                foreach (var enabled in new[] { false, true, false })
                {
                    backdrop.SetTransparencyEnabled(enabled);
                    await Task.Delay(80);
                    Require(backdrop.EffectiveInputActive == enabled, "A live transparency switch did not reach the acrylic controller.");
                    Require(enabled || !backdrop.IsMaterialActive, "Disabling transparency left acrylic active.");
                    Require(backdrop.IsGrainEnabled == (enabled && grainEnabled), "Grain did not follow the transparency preference.");
                    Require(((FrameworkElement)window.Root.FindName("OpaqueBackdrop")).Visibility
                        == (enabled ? Visibility.Collapsed : Visibility.Visible), "The opaque background did not follow the transparency preference.");
                    window.RequireGrainConfiguration();
                    window.RequireNativeRecipe();
                    if (!enabled)
                        await TrayMenuSmokeCheck.CaptureAsync(window.Root, directory,
                            $"{(menu ? "menu" : "brightness")}-opaque-{appearance.ToString().ToLowerInvariant()}.png");
                    Require(window.WindowHandle == handle && ReferenceEquals(window.PanelSystemBackdrop, backdrop),
                        "A transparency switch replaced the window or backdrop.");
                }

            }

            window.Root.RequestedTheme = ElementTheme.Default;
            await Task.Delay(80);
            window.RequireNativeRecipe();

            // The Windows notification can arrive on a worker thread while the flyout is hidden.
            window.Hide();
            backdrop.SetTransparencyEnabled(!window.SystemTransparencyEnabled);
            await window.DeliverTransparencyNotificationForTestAsync();
            await Task.Delay(80);
            Require(backdrop.TransparencyEnabled == window.SystemTransparencyEnabled && backdrop.EffectiveInputActive == false,
                "A background settings notification was lost while the flyout was hidden.");
            if (menu) await window.ShowTrayMenuAsync();
            else await window.ShowNearTrayAsync();
            Require(backdrop.EffectiveInputActive == window.SystemTransparencyEnabled,
                "Reopening ignored the current transparency preference.");
            window.RequireGrainConfiguration();
            window.RequireNativeRecipe();
        }
        finally
        {
            backdrop.SetTransparencyEnabled(window.SystemTransparencyEnabled);
            window.Root.RequestedTheme = theme;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
