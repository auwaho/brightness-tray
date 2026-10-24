using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace BrightnessTray.App;

// The clear window backdrop prevents an opaque flash before the acrylic panel is ready.
internal sealed class TransparentWindowBackdrop : SystemBackdrop
{
    private global::Windows.UI.Composition.Compositor? _compositor;
    private global::Windows.UI.Composition.CompositionColorBrush? _brush;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        DispatcherQueue.GetForCurrentThread().EnsureSystemDispatcherQueue();
        // The system backdrop target uses the Windows compositor, distinct from XAML's compositor.
        _compositor = new global::Windows.UI.Composition.Compositor();
        _brush = _compositor.CreateColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
        target.SystemBackdrop = _brush;
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        // This transparent host has no theme policy and must not query a target during teardown.
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        var brush = _brush;
        var compositor = _compositor;
        _brush = null;
        _compositor = null;
        try
        {
            target.SystemBackdrop = null;
            base.OnTargetDisconnected(target);
        }
        finally
        {
            try { brush?.Dispose(); }
            finally { compositor?.Dispose(); }
        }
    }
}
