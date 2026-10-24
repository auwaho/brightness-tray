using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;

namespace BrightnessTray.App;

// Compiled only by the UI test runner; observes the unmodified flyout motion.
public sealed partial class FlyoutWindow
{
    internal void RequireGrainConfiguration() => _grain.RequireConfiguration(_backdrop.IsMaterialActive, FlyoutRoot.ActualTheme);
    internal Task InvokeTrayMenuCommandForTestAsync(string command) => RunTrayMenuCommandAsync(command);
    internal Task ReplayStationaryTrayPressAsync()
    {
        // Simulate the delayed callback at the original click position, even if the user moves the mouse.
        _taskbarDismissedPosition = System.Windows.Forms.Cursor.Position;
        return ToggleFromTrayAsync();
    }
    internal Task OpeningTask => _showTask ?? Task.CompletedTask;
    internal bool BackdropIsActive => _backdrop.IsInputActive && _backdrop.IsConnected;
    internal bool BackdropIsConnected => _backdrop.IsConnected;
    internal Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme? BackdropTheme => _backdrop.Theme;
    internal bool DisplayRowsFit => DisplayList.ActualHeight <= DisplayScroller.ViewportHeight + 1;
    internal int RevealFrameCount { get; private set; }
    internal bool RevealKeptSize { get; private set; }
    internal bool RevealMonotonic { get; private set; }
    internal global::Windows.Graphics.PointInt32 RevealStartPosition { get; private set; }
    internal global::Windows.Graphics.PointInt32 RevealDestination { get; private set; }
    internal double? RevealHalfwayRemaining { get; private set; }
    internal double RevealDistance { get; private set; }
    internal int DismissFrameCount { get; private set; }
    internal bool DismissKeptSize { get; private set; }
    internal bool DismissMonotonic { get; private set; }
    internal double? DismissHalfwayRemaining { get; private set; }
    internal Microsoft.UI.Xaml.Controls.Grid Root => FlyoutRoot;
    internal Microsoft.UI.Xaml.Controls.Grid PanelRoot => LayoutRoot;
    internal SystemBackdrop? PanelSystemBackdrop { get => PanelBackdrop.SystemBackdrop; set => PanelBackdrop.SystemBackdrop = value; }
    internal bool FooterFits => FooterPanel.TransformToVisual(LayoutRoot)
        .TransformPoint(new global::Windows.Foundation.Point(0, 0)).Y + FooterPanel.ActualHeight <= LayoutRoot.ActualHeight + 1;
    internal void ObserveMotion()
    {
        TaskCompletionSource? observed = null;
        var initialOffset = 0;
        var closestToHalfway = 1.0;
        var previousRemaining = 1.0;
        EventHandler<object> observe = (_, _) =>
        {
            if (_slideCompletion is null) return;
            var closing = _closing;
            if (observed != _slideCompletion)
            {
                observed = _slideCompletion;
                initialOffset = AppWindow.Position.Y - _slideDestination.Y;
                closestToHalfway = 1;
                previousRemaining = 1;
                if (closing)
                {
                    DismissFrameCount = 0;
                    DismissKeptSize = true;
                    DismissMonotonic = true;
                    DismissHalfwayRemaining = null;
                }
                else
                {
                    RevealFrameCount = 0;
                    RevealKeptSize = true;
                    RevealMonotonic = true;
                    RevealStartPosition = AppWindow.Position;
                    RevealDestination = new global::Windows.Graphics.PointInt32(_slideDestination.X, _slideDestination.Y);
                    RevealHalfwayRemaining = null;
                    RevealDistance = initialOffset / NativeWindowPosition.GetScale(WindowHandle);
                }
            }
            var keptSize = AppWindow.Size.Width == _slideDestination.Width && AppWindow.Size.Height == _slideDestination.Height;
            if (closing) { DismissFrameCount++; DismissKeptSize &= keptSize; }
            else { RevealFrameCount++; RevealKeptSize &= keptSize; }
            if (initialOffset == 0) return;
            var remaining = (AppWindow.Position.Y - _slideDestination.Y) / (double)initialOffset;
            var tolerance = 1.0 / Math.Abs(initialOffset);
            var monotonic = remaining >= -tolerance && remaining <= previousRemaining + tolerance;
            if (closing) DismissMonotonic &= monotonic;
            else RevealMonotonic &= monotonic;
            previousRemaining = remaining;
            // A short animation need not render a frame in a particular progress band.
            // Keep the nearest interior sample as diagnostics; exact linearity is covered
            // by deterministic checks of the production interpolation helper.
            if (remaining is > 0 and < 1 && Math.Abs(remaining - 0.5) < closestToHalfway)
            {
                closestToHalfway = Math.Abs(remaining - 0.5);
                if (closing) DismissHalfwayRemaining = remaining;
                else RevealHalfwayRemaining = remaining;
            }
        };
        CompositionTarget.Rendering += observe;
        Closed += (_, _) => CompositionTarget.Rendering -= observe;
    }
}
