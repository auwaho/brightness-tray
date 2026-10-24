using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace BrightnessTray.App;

internal sealed partial class FlyoutGrain
{
    internal void RequireConfiguration(bool active, ElementTheme theme)
    {
        var expectedOpacity = active ? theme == ElementTheme.Dark ? 0.014f : 0.006f : 0;
        if (Math.Abs(_visual.Opacity - expectedOpacity) > 0.00001f
            || _visual.Size != Vector2.Zero || _visual.RelativeSizeAdjustment != Vector2.One
            || _brush.Stretch != CompositionStretch.None
            || ElementCompositionPreview.GetElementChildVisual(_host) != _visual
            || _surface.DecodedSize.Width <= 0 || _surface.DecodedSize.Height <= 0)
            throw new InvalidOperationException($"Composition grain: opacity={_visual.Opacity}, expected={expectedOpacity}, size={_visual.Size}, relative={_visual.RelativeSizeAdjustment}, stretch={_brush.Stretch}, attached={ElementCompositionPreview.GetElementChildVisual(_host) == _visual}, decoded={_surface.DecodedSize}.");
    }
}
