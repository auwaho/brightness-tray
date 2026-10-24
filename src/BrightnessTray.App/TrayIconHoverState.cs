using System.Drawing;

namespace BrightnessTray.App;

internal sealed class TrayIconHoverState
{
    private Rectangle? _bounds;
    private bool _inside;

    // Bounds/hit-test samples may disappear while Shell shows a tooltip. A real exit
    // requires leaving the last confirmed rectangle; wheel capture keeps its stricter checks.
    internal bool Entered(Rectangle? bounds, Point pointer)
    {
        if (bounds.HasValue) _bounds = bounds;
        var inside = (_inside ? _bounds : bounds)?.Contains(pointer) == true;
        var entered = inside && !_inside;
        _inside = inside;
        if (!inside) _bounds = null;
        return entered;
    }

    internal void Reset()
    {
        _bounds = null;
        _inside = false;
    }
}
