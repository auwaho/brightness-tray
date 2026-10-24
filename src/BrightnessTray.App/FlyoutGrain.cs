using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace BrightnessTray.App;

internal sealed partial class FlyoutGrain : IDisposable
{
    private readonly UIElement _host;
    private readonly LoadedImageSurface _surface;
    private readonly CompositionSurfaceBrush _brush;
    private readonly SpriteVisual _visual;

    internal FlyoutGrain(UIElement host)
    {
        _host = host;
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _surface = LoadedImageSurface.StartLoadFromUri(new Uri("ms-appx:///Assets/Grain.png"));
        _brush = compositor.CreateSurfaceBrush(_surface);
        _brush.Stretch = CompositionStretch.None;
        _brush.HorizontalAlignmentRatio = 0;
        _brush.VerticalAlignmentRatio = 0;
        _visual = compositor.CreateSpriteVisual();
        _visual.Brush = _brush;
        _visual.RelativeSizeAdjustment = Vector2.One;
        _visual.Opacity = 0;
        // A composition child adds texture without changing XAML measurement or input handling.
        ElementCompositionPreview.SetElementChildVisual(host, _visual);
    }

    internal void SetMaterial(bool active, ElementTheme theme) =>
        _visual.Opacity = active ? theme == ElementTheme.Dark ? 0.014f : 0.006f : 0;

    public void Dispose()
    {
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        _visual.Dispose();
        _brush.Dispose();
        _surface.Dispose();
    }
}
