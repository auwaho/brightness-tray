using BrightnessTray.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace BrightnessTray.App.Controls;

public sealed class BrightnessSlider : Slider
{
    public BrightnessSlider() => DefaultStyleKey = typeof(Slider);

    protected override void OnPointerWheelChanged(PointerRoutedEventArgs args)
    {
        var input = args.GetCurrentPoint(this).Properties;
        // Replace Slider's built-in wheel step so one notch cannot apply twice or scroll the list.
        if (!input.IsHorizontalMouseWheel && ApplyWheelDelta(input.MouseWheelDelta)) args.Handled = true;
    }

    internal bool ApplyWheelDelta(int delta) => IsEnabled && IsHitTestVisible
        && DataContext is DisplayViewModel display && display.AdjustBrightnessFromWheel(delta);
}
