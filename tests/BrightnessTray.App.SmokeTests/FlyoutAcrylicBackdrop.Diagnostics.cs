using System;
using Microsoft.UI.Composition.SystemBackdrops;
using Windows.UI;

namespace BrightnessTray.App;

internal sealed partial class FlyoutAcrylicBackdrop
{
    internal bool IsInputActive => _isInputActive;
    internal bool IsConnected => _controller is not null;
    internal SystemBackdropTheme? Theme => _configuration?.Theme;
    internal bool? EffectiveInputActive => _configuration?.IsInputActive;
    internal float NativeTintOpacity => _controller!.TintOpacity;
    internal object? NativeRecipe => _controller is not { } controller ? null : new
    {
        kind = controller.Kind.ToString(),
        theme = _configuration?.Theme.ToString(),
        tint = controller.TintColor.ToString(),
        tintOpacity = controller.TintOpacity,
        luminosityOpacity = controller.LuminosityOpacity,
        fallback = controller.FallbackColor.ToString(),
        state = controller.State.ToString(),
    };

    internal void RequireNativeRecipe(SystemBackdropTheme expectedTheme)
    {
        if (_controller is not { } controller || _configuration is not { } configuration)
            throw new InvalidOperationException("The acrylic controller is not connected.");
        // Measured with connected controllers on the pinned runtime; unconnected controllers can retain Light defaults.
        // Fixtures: artifacts/validation/acrylic-tuning/baseline/acrylic-{light,dark}.json.
        var dark = expectedTheme == SystemBackdropTheme.Dark;
        var expectedTint = dark ? Color.FromArgb(255, 44, 44, 44) : Color.FromArgb(255, 252, 252, 252);
        var expectedFallback = dark ? expectedTint : Color.FromArgb(255, 249, 249, 249);
        var expectedLuminosity = dark ? 0.96f : 0.85f;
        var expectedOpacity = dark ? 0.50f : 0;
        if (configuration.Theme != expectedTheme || controller.Kind != DesktopAcrylicKind.Default
            || !controller.TintColor.Equals(expectedTint)
            || Math.Abs(controller.LuminosityOpacity - expectedLuminosity) > 0.0001f
            || !controller.FallbackColor.Equals(expectedFallback)
            || Math.Abs(controller.TintOpacity - expectedOpacity) > 0.0001f)
            throw new InvalidOperationException($"The {expectedTheme} acrylic recipe changed an unexpected native property or has the wrong tint opacity.");
    }

}
