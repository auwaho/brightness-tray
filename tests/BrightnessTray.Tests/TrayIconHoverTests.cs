using System.Drawing;
using BrightnessTray.App;

internal static class TrayIconHoverTests
{
    internal static Task StableEntryAsync()
    {
        var hover = new TrayIconHoverState();
        var icon = new Rectangle(100, 100, 24, 24);
        var pointer = new Point(110, 110);
        Equal(false, hover.Entered(null, pointer));
        Equal(true, hover.Entered(icon, pointer));
        for (var sample = 0; sample < 100; sample++)
        {
            Equal(false, hover.Entered(null, pointer));
            Equal(false, hover.Entered(icon, pointer));
        }
        Equal(false, hover.Entered(null, new Point(150, 110)));
        Equal(false, hover.Entered(null, pointer));
        Equal(true, hover.Entered(icon, pointer));
        // A confirmed move of the icon invalidates the previous rectangle.
        var moved = new Rectangle(200, 100, 24, 24);
        Equal(false, hover.Entered(moved, pointer));
        Equal(false, hover.Entered(null, new Point(210, 110)));
        Equal(true, hover.Entered(moved, new Point(210, 110)));
        // Menu opening, hiding and Explorer registration reset permit a fresh entry.
        hover.Reset();
        Equal(false, hover.Entered(null, new Point(210, 110)));
        Equal(true, hover.Entered(moved, new Point(210, 110)));
        return Task.CompletedTask;
    }

    private static void Equal(bool expected, bool actual)
    {
        if (expected != actual) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
