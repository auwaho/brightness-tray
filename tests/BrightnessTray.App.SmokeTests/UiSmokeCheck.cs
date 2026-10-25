using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace BrightnessTray.App;

// Opt-in UI integration check. App selects virtual devices before this can run.
internal static class UiSmokeCheck
{
    public static async Task RunAsync(FlyoutWindow window, DemoDisplayDiscovery discovery,
        BackdropCallbackErrorTracker backdropErrors, string outputDirectory, bool nativeCapture)
    {
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "result.json"), "{\"success\": false}");
        File.WriteAllText(Path.Combine(outputDirectory, "failure.txt"), string.Empty);
        await window.OpeningTask.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(250);
        backdropErrors.ThrowIfAny();
        if (!window.IsVisible || !window.BackdropIsActive)
            throw new InvalidOperationException($"The flyout did not open with its active acrylic configuration. Visible={window.IsVisible}, ActiveBackdrop={window.BackdropIsActive}, Foreground={NativeWindowPosition.ForegroundWindow}, Window={window.WindowHandle}, Frames={window.RevealFrameCount}.");
        RequireNoNativeFrame(window);
        window.RequireGrainConfiguration();
        if (window.AnimationsEnabled && (window.RevealFrameCount == 0 || !window.RevealKeptSize || !window.RevealMonotonic))
            throw new InvalidOperationException("The flyout did not animate monotonically at a fixed size.");
        var initialRevealFrames = window.RevealFrameCount;
        var openingMotionMonotonic = window.RevealMonotonic;
        var halfwayRemaining = window.RevealHalfwayRemaining;
        var revealDistance = window.RevealDistance;
        if (window.AnimationsEnabled && (Math.Abs(revealDistance - window.Root.ActualHeight - 12) > 1
            || window.RevealStartPosition.Y <= window.RevealDestination.Y
            || window.AppWindow.Position != window.RevealDestination))
            throw new InvalidOperationException($"The opening motion did not travel its full distance to the final position: distance={revealDistance}, start={window.RevealStartPosition}, destination={window.RevealDestination}, actual={window.AppWindow.Position}.");
        if (!window.DispatcherQueue.HasThreadAccess || window.ViewModel.Displays.Count != 2)
            throw new InvalidOperationException("Demo did not create two displays on the WinUI thread.");
        if (nativeCapture) await NativeTransparencyCheck.RunAsync(window, outputDirectory);
        var sliders = Descendants(window.Root).OfType<Slider>().ToArray();
        if (sliders.Length != 2 || sliders[0].Value != 65 || sliders[1].Value != 80)
            throw new InvalidOperationException("The native sliders did not receive their initial binding values.");
        RequireSliderThumbDiameter(sliders);
        if (!window.DisplayRowsFit)
            throw new InvalidOperationException("The initial flyout clipped one of its two display cards.");
        RequireCompactFooter(window);
        await RequireRefreshStatusDoesNotMoveCardsAsync(window, discovery, outputDirectory);
        // Refresh replaces row bindings; use the new controls for subsequent slider checks.
        sliders = Descendants(window.Root).OfType<Slider>().ToArray();
        var wheelSliders = sliders.OfType<Controls.BrightnessSlider>().ToArray();
        if (wheelSliders.Length != 2 || !wheelSliders[0].ApplyWheelDelta(60) || wheelSliders[0].Value != 65
            || !wheelSliders[0].ApplyWheelDelta(60) || wheelSliders[0].Value != 70 || wheelSliders[1].Value != 80
            || !wheelSliders[0].ApplyWheelDelta(-120) || wheelSliders[0].Value != 65)
            throw new InvalidOperationException("Slider wheel input lost fine deltas, changed another display, or used the wrong step.");
        wheelSliders[1].ApplyWheelDelta(-120);
        if (wheelSliders[1].Value != 75 || wheelSliders[0].Value != 65)
            throw new InvalidOperationException("Scrolling the second slider changed the first display.");
        wheelSliders[1].ApplyWheelDelta(120);
        wheelSliders[0].IsEnabled = false;
        if (wheelSliders[0].ApplyWheelDelta(120) || wheelSliders[0].Value != 65)
            throw new InvalidOperationException("A disabled slider accepted wheel input.");
        wheelSliders[0].IsEnabled = true;

        var notificationsOnUiThread = true;
        window.ViewModel.Displays[0].PropertyChanged += (_, _) =>
            notificationsOnUiThread &= window.DispatcherQueue.HasThreadAccess;
        sliders[0].Value = 73;
        await Task.Delay(30);
        var captions = Descendants(window.Root).OfType<TextBlock>().Select(text => text.Text).ToArray();
        if (window.ViewModel.Displays.Any(row => captions.Contains(row.ConnectionLabel))
            || captions.Any(text => text.StartsWith("Applying", StringComparison.Ordinal)))
            throw new InvalidOperationException("A compact display card shows a connection or applying caption.");
        await window.ViewModel.RefreshAsync();
        await Task.Delay(150);
        if (!notificationsOnUiThread || window.ViewModel.Displays[0].Brightness != 73)
            throw new InvalidOperationException("Slider binding or drain-before-refresh failed.");

        var originalSize = window.AppWindow.Size;
        var scale = window.Root.XamlRoot.RasterizationScale;
        window.AppWindow.Resize(new global::Windows.Graphics.SizeInt32(originalSize.Width, (int)(240 * scale)));
        await Task.Delay(100);
        if (!window.FooterFits) throw new InvalidOperationException("The footer is clipped in a small work area.");
        RequireCompactFooter(window);
        window.AppWindow.Resize(originalSize);
        await Task.Delay(100);
        RequireNoNativeFrame(window);

        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            window.Root.RequestedTheme = theme;
            await Task.Delay(100);
            RequireBackdropTheme(window);
            backdropErrors.ThrowIfAny();
            RequireNoNativeFrame(window);
            RequireCompactFooter(window);
            RequireSliderThumbDiameter(Descendants(window.Root).OfType<Slider>());
            // RenderTargetBitmap excludes the desktop backdrop; supply its solid fallback for the preview.
            window.PanelRoot.Background = new SolidColorBrush(theme == ElementTheme.Light
                ? global::Windows.UI.Color.FromArgb(255, 243, 243, 243)
                : global::Windows.UI.Color.FromArgb(255, 32, 32, 32));
            await CaptureAsync(window.Root, outputDirectory, $"winui-{theme.ToString().ToLowerInvariant()}.png");
        }

        var fullHeight = window.AppWindow.Size.Height;
        var anchoredBottom = window.AppWindow.Position.Y + fullHeight;
        var internalWritesBeforeUnavailable = discovery.InternalWriteCount;
        discovery.InternalAvailable = false;
        // Background tray refreshes bypass the window's explicit refresh/resize method.
        await window.ViewModel.RefreshAsync();
        await Task.Delay(100);
        sliders = Descendants(window.Root).OfType<Slider>().ToArray();
        if (sliders.Length != 1 || window.ViewModel.Displays.Count != 1 || window.ViewModel.Displays[0].Id != "demo-external")
            throw new InvalidOperationException("An unavailable display remained in the visible list.");
        if (discovery.InternalWriteCount != internalWritesBeforeUnavailable)
            throw new InvalidOperationException("A hidden unavailable display sent a brightness write.");
        if (window.AppWindow.Size.Height >= fullHeight
            || window.AppWindow.Position.Y + window.AppWindow.Size.Height != anchoredBottom || !window.FooterFits)
            throw new InvalidOperationException("Removing a display did not shrink the flyout at its tray anchor.");
        await CaptureAsync(window.Root, outputDirectory, "winui-unavailable.png");
        discovery.InternalAvailable = true;
        await window.ViewModel.RefreshAsync();
        await Task.Delay(100);
        if (window.AppWindow.Size.Height != fullHeight
            || window.AppWindow.Position.Y + window.AppWindow.Size.Height != anchoredBottom)
            throw new InvalidOperationException("Restoring a display did not restore the flyout at its tray anchor.");
        var recoveredSlider = Descendants(window.Root).OfType<Slider>().Last();
        if (!recoveredSlider.IsEnabled || !recoveredSlider.IsHitTestVisible
            || ToolTipService.GetToolTip(VisualTreeHelper.GetParent(recoveredSlider)) is not null)
            throw new InvalidOperationException("A recovered display did not regain its slider.");
        await using (var wheel = new ViewModels.TrayBrightnessController(window.ViewModel))
        {
            await wheel.ScrollAsync(120);
            var rows = window.ViewModel.Displays.ToArray();
            await Task.WhenAll(rows.Select(row => row.PauseAndDrainAsync()));
            foreach (var row in rows) row.Resume();
            if (rows[0].Brightness != 78 || rows[1].Brightness != 85 || !window.IsVisible)
                throw new InvalidOperationException("Tray scrolling did not adjust both displays by five percentage points.");
            await wheel.ScrollAsync(-120);
            await Task.WhenAll(rows.Select(row => row.PauseAndDrainAsync()));
            foreach (var row in rows) row.Resume();
            if (rows[0].Brightness != 73 || rows[1].Brightness != 80)
                throw new InvalidOperationException("Reverse scrolling did not subtract five percentage points from both displays.");
        }
        window.PanelRoot.Background = null;
        window.Root.RequestedTheme = ElementTheme.Default;
        await Task.Delay(100);
        RequireBackdropTheme(window);

        await TrayBrightnessToolTipSmokeCheck.RunAsync(window);

        // Clearing and reattaching exercises configuration callbacks during native target teardown.
        File.WriteAllText(Path.Combine(outputDirectory, "phase.txt"), "backdrop reconnect");
        var backdrop = window.PanelSystemBackdrop;
        window.PanelSystemBackdrop = null;
        await Task.Delay(100);
        window.RequireGrainConfiguration();
        if (window.BackdropIsConnected)
            throw new InvalidOperationException("The acrylic controller remained connected after clearing its target.");
        window.Root.RequestedTheme = ElementTheme.Light;
        await Task.Delay(100);
        window.PanelSystemBackdrop = backdrop;
        await Task.Delay(100);
        if (!window.BackdropIsConnected || !window.BackdropIsActive)
            throw new InvalidOperationException("The acrylic controller did not reconnect with its active configuration.");
        RequireBackdropTheme(window);
        window.Root.RequestedTheme = ElementTheme.Default;
        await Task.Delay(100);
        RequireBackdropTheme(window);
        backdropErrors.ThrowIfAny();
        RequireNoNativeFrame(window);

        await RequireTrayCloseAnimationAsync(window);
        File.WriteAllText(Path.Combine(outputDirectory, "phase.txt"), "tray reversal");
        var dismissFrames = window.DismissFrameCount;
        var dismissHalfwayRemaining = window.DismissHalfwayRemaining;
        var closingMotionMonotonic = window.DismissMonotonic;
        var interruptedOpen = window.ToggleFromTrayAsync();
        await window.ToggleFromTrayAsync();
        var resumedOpen = window.ToggleFromTrayAsync();
        await Task.WhenAll(interruptedOpen, resumedOpen).WaitAsync(TimeSpan.FromSeconds(5));
        if (!window.IsVisible) throw new InvalidOperationException("Rapid tray presses did not reopen the canceled flyout.");
        window.DismissOnDeactivation(taskbarPointerDown: true);
        await window.ToggleFromTrayAsync();
        if (window.IsVisible) throw new InvalidOperationException("A tray press reopened the flyout after taskbar deactivation hid it.");
        await window.ToggleFromTrayAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (!window.IsVisible) throw new InvalidOperationException("The next tray press did not reopen the flyout.");
        if (window.AnimationsEnabled)
        {
            // Reverse an in-progress dismissal without jumping back to either endpoint.
            var openPosition = window.AppWindow.Position;
            var closing = window.ToggleFromTrayAsync();
            await WaitForMotionAsync(window, opening: false);
            var turningPosition = window.AppWindow.Position;
            var reopening = window.ToggleFromTrayAsync();
            if (window.AppWindow.Position != turningPosition)
                throw new InvalidOperationException("Reversing a closing animation jumped to an endpoint.");
            await Task.WhenAll(closing, reopening).WaitAsync(TimeSpan.FromSeconds(5));
            if (!window.IsVisible || window.AppWindow.Position != openPosition || !window.BackdropIsActive)
                throw new InvalidOperationException("An interrupted closing later hid the reopened flyout.");

            window.Hide();
            var opening = window.ToggleFromTrayAsync();
            await WaitForMotionAsync(window, opening: true);
            turningPosition = window.AppWindow.Position;
            closing = window.ToggleFromTrayAsync();
            if (window.AppWindow.Position != turningPosition)
                throw new InvalidOperationException("Reversing an opening animation jumped to an endpoint.");
            await Task.WhenAll(opening, closing).WaitAsync(TimeSpan.FromSeconds(5));
            if (window.IsVisible) throw new InvalidOperationException("An interrupted opening later restored the dismissed flyout.");
            await window.ToggleFromTrayAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        // The shell callback can arrive after the 80 ms dismissal has already finished.
        File.WriteAllText(Path.Combine(outputDirectory, "phase.txt"), "delayed tray callback");
        window.DismissOnDeactivation(taskbarPointerDown: true);
        await Task.Delay(150);
        await window.ReplayStationaryTrayPressAsync();
        if (window.IsVisible) throw new InvalidOperationException("A delayed tray callback reopened the dismissed flyout.");
        await window.ToggleFromTrayAsync().WaitAsync(TimeSpan.FromSeconds(5));
        RequireNoNativeFrame(window);

        // Activating another window exercises the same deactivation path as clicking outside the flyout.
        File.WriteAllText(Path.Combine(outputDirectory, "phase.txt"), "outside activation");
        await using var focusTarget = new FocusTargetWindow();
        try
        {
            File.WriteAllText(Path.Combine(outputDirectory, "phase.txt"), "show focus target");
            await focusTarget.ShowAsync(outputDirectory);
            await Task.Delay(100);
            File.WriteAllText(Path.Combine(outputDirectory, "phase.txt"), "activate focus target");
            var outsideClosePosition = window.AppWindow.Position;
            var outsideCloseSize = window.AppWindow.Size;
            await focusTarget.ActivateAsync();
            File.WriteAllText(Path.Combine(outputDirectory, "phase.txt"), "focus target activated");
            if (window.AnimationsEnabled && (!window.IsVisible || !window.BackdropIsActive))
                throw new InvalidOperationException("Clicking outside hid the flyout before its closing animation.");
            await Task.Delay(150);
            File.WriteAllText(Path.Combine(outputDirectory, "phase.txt"), "outside closing finished");
            if (window.IsVisible) throw new InvalidOperationException("The flyout did not hide after losing focus.");
            if (window.AnimationsEnabled && (window.DismissFrameCount == 0 || !window.DismissKeptSize || !window.DismissMonotonic
                || window.AppWindow.Position.X != outsideClosePosition.X
                || window.AppWindow.Position.Y - outsideClosePosition.Y != outsideCloseSize.Height + (int)Math.Ceiling(12 * scale)
                || window.AppWindow.Size != outsideCloseSize))
                throw new InvalidOperationException($"Clicking outside did not use the reverse linear closing animation: frames={window.DismissFrameCount}, fixedSize={window.DismissKeptSize}, halfway={window.DismissHalfwayRemaining}, position={window.AppWindow.Position.Y}, start={outsideClosePosition.Y}.");
            File.WriteAllText(Path.Combine(outputDirectory, "phase.txt"), "close focus target");
            await focusTarget.DisposeAsync();
            File.WriteAllText(Path.Combine(outputDirectory, "phase.txt"), "focus target closed");
            await Task.Delay(100);
            File.WriteAllText(Path.Combine(outputDirectory, "phase.txt"), "reopen after outside");
            await window.ShowNearTrayAsync().WaitAsync(TimeSpan.FromSeconds(5));
            File.WriteAllText(Path.Combine(outputDirectory, "phase.txt"), "outside reopening finished");
            if (!window.IsVisible || !window.BackdropIsActive)
                throw new InvalidOperationException("The reopened flyout did not restore its active acrylic configuration.");
            RequireBackdropTheme(window);
            backdropErrors.ThrowIfAny();
            RequireNoNativeFrame(window);
            var foregroundAcquired = NativeWindowPosition.ForegroundWindow == window.WindowHandle;
            File.WriteAllText(Path.Combine(outputDirectory, "focus.json"), JsonSerializer.Serialize(new { foregroundAcquired }));
            window.Hide();
            File.WriteAllText(Path.Combine(outputDirectory, "phase.txt"), "cancel opening");
            var canceledOpen = window.ShowNearTrayAsync();
            window.Hide();
            await canceledOpen.WaitAsync(TimeSpan.FromSeconds(5));
            if (window.IsVisible) throw new InvalidOperationException("A canceled opening made the flyout visible again.");
        }
        finally { await focusTarget.DisposeAsync(); }
        File.WriteAllText(Path.Combine(outputDirectory, "phase.txt"), "write final result");
        backdropErrors.ThrowIfAny();
        File.WriteAllText(Path.Combine(outputDirectory, "result.json"), JsonSerializer.Serialize(new
        {
            success = true,
            ui = "WinUI 3",
            displayCount = window.ViewModel.Displays.Count,
            slidersRealized = sliders.Length,
            finalBrightness = window.ViewModel.Displays[0].Brightness,
            notificationsOnUiThread,
            constrainedLayout = true,
            hidesOnDeactivation = true,
            outsideClickUsesClosingAnimation = window.AnimationsEnabled,
            startupTrayMenuTested = true,
            startupFailureKeepsCheckmark = true,
            startupRespectsPackagedUserAndPolicyStates = true,
            startupRegistryRoundTripInIsolatedKey = true,
            windowsAutostartChanged = false,
            nativeClientFillsWindow = true,
            activeBackdropOnOpen = true,
            initialDisplayRowsFit = true,
            inactivePreviewAllowed = true,
            initialRevealFrames,
            halfwayRemaining,
            openingMotionMonotonic,
            revealDistance,
            openingAnimationDurationMs = FlyoutWindow.AnimationDurationMs,
            closingAnimationDurationMs = FlyoutWindow.AnimationDurationMs,
            dismissFrames,
            dismissHalfwayRemaining,
            closingMotionMonotonic,
            trayDismissalReversesOpening = window.AnimationsEnabled,
            interruptedAnimationsReverseWithoutJump = window.AnimationsEnabled,
            sliderOuterThumbDiameter = 20,
            slidesFullWindowFromTaskbar = true,
            revealKeptSize = true,
            unavailableDisplaysHidden = true,
            displayChangesPreserveTrayAnchor = true,
            fixedStepWheelOnAllDisplays = true,
            nativeTrayBrightnessToolTip = true,
            nativeTrayWheelLifecycle = true,
            connectionCaptionsHidden = true,
            wheelOverEachSlider = true,
            iconOnlyRefreshInFullWidthFooter = true,
            footerHeight = 50,
            darkFooterDivider = true,
            subtleCompositionGrain = true,
            grainFollowsAcrylicStateAndTheme = true,
            lightCardStrokeInDarkTheme = true,
            refreshStatusInFooterWithoutLayoutShift = true,
            refreshIconSize = 14,
            trayIconFillsSlot = nativeCapture,
            nativeShadowMarginTransparent = nativeCapture,
            whiteHollowTrayIcon = nativeCapture,
            footerPreservesBackdropTint = true,
            translucentPanelStroke = true,
            nativeWindowShadow = nativeCapture,
            nativeCornerAntialiasing = nativeCapture,
            nativeOpeningFrameChecked = nativeCapture && window.AnimationsEnabled,
            nativeCaptureRequested = nativeCapture,
            trayPressTogglesVisibility = true,
            trayDeactivationDoesNotReopen = true,
            rapidTrayPressesRecover = true,
            availabilityRecovered = true,
            reopenedWithActiveBackdrop = true,
            canceledOpeningStaysHidden = true,
            backdropDetachedAndReattached = true,
            backdropThemeFollowsWindow = true,
            backdropCallbackErrors = backdropErrors.Count,
            runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
            winUIRuntimePath = System.Diagnostics.Process.GetCurrentProcess().Modules
                .Cast<System.Diagnostics.ProcessModule>().Single(module =>
                    string.Equals(module.ModuleName, "Microsoft.ui.xaml.dll", StringComparison.OrdinalIgnoreCase)).FileName,
            hardwareWrites = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task RequireTrayCloseAnimationAsync(FlyoutWindow window)
    {
        var start = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        var scale = window.Root.XamlRoot.RasterizationScale;
        var closing = window.ToggleFromTrayAsync();
        if (window.AnimationsEnabled && (!window.IsVisible || !window.BackdropIsActive))
            throw new InvalidOperationException("The tray dismissal hid the window or disabled acrylic before sliding down.");
        var previousY = start.Y;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!closing.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The closing animation did not finish.");
            await Task.Delay(5);
            var position = window.AppWindow.Position;
            if (position.Y < previousY || position.X != start.X || window.AppWindow.Size != size)
                throw new InvalidOperationException("The reverse tray animation changed direction, width, or height.");
            previousY = position.Y;
            if (!closing.IsCompleted && (!window.IsVisible || !window.BackdropIsActive))
                throw new InvalidOperationException("The closing flyout vanished before the reverse motion completed.");
        }
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        if (window.IsVisible) throw new InvalidOperationException("A tray press did not hide the open flyout.");
        if (window.AnimationsEnabled && (window.DismissFrameCount == 0 || !window.DismissKeptSize || !window.DismissMonotonic
            || previousY - start.Y != size.Height + (int)Math.Ceiling(12 * scale)))
            throw new InvalidOperationException("The closing flyout did not reverse the full linear opening motion.");
    }

    // A different app owns its input thread; do not mix another UI window into the flyout's dispatcher.
    internal sealed class FocusTargetWindow : IAsyncDisposable
    {
        private Process? _process;
        internal IntPtr Handle { get; private set; }

        internal async Task ShowAsync(string outputDirectory, params string[] options)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath
                ?? throw new InvalidOperationException("The UI test executable path is unavailable."))
                { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(Path.Combine(outputDirectory, "focus-target"));
            start.ArgumentList.Add("--focus-target");
            foreach (var option in options) start.ArgumentList.Add(option);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the outside focus target.");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !_process.HasExited)
            {
                _process.Refresh();
                Handle = _process.MainWindowHandle;
                if (Handle != IntPtr.Zero) return;
                await Task.Delay(25);
            }
            throw new InvalidOperationException("The outside focus target did not create its window.");
        }

        internal async Task ActivateAsync()
        {
            if (!NativeWindowPosition.TrySetForeground(Handle))
                throw new InvalidOperationException("Could not activate the outside focus target.");
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (NativeWindowPosition.ForegroundWindow == Handle) return;
                await Task.Delay(1);
            }
            throw new InvalidOperationException("The outside focus target did not become the foreground window.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_process is not { } process) return;
            _process = null;
            try
            {
                if (process.HasExited) return;
                process.CloseMainWindow();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { process.Kill(); await process.WaitForExitAsync(); }
            }
            finally { process.Dispose(); }
        }
    }

    private static async Task WaitForMotionAsync(FlyoutWindow window, bool opening)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var frames = opening ? window.RevealFrameCount : window.DismissFrameCount;
            if (window.IsVisible && frames > 0 && (opening ? !window.OpeningTask.IsCompleted : true)) return;
            await Task.Delay(5);
        }
        throw new InvalidOperationException("The flyout did not enter the expected animation.");
    }

    private static void RequireBackdropTheme(FlyoutWindow window)
    {
        var expected = window.Root.ActualTheme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        if (window.BackdropTheme != expected)
            throw new InvalidOperationException($"The acrylic theme did not follow the window: expected {expected}, received {window.BackdropTheme}.");
        window.RequireNativeRecipe();
        window.RequireGrainConfiguration();
    }

    private static void RequireCompactFooter(FlyoutWindow window)
    {
        var footer = (Border)window.Root.FindName("FooterPanel");
        var refresh = (Button)window.Root.FindName("RefreshButton");
        var stroke = (Border)window.Root.FindName("SurfaceStroke");
        var surface = (Border)window.Root.FindName("SurfaceBorder");
        var expectedDividerAlpha = window.Root.ActualTheme == ElementTheme.Dark ? 78 : 36;
        var expectedFooterAlpha = window.Root.ActualTheme == ElementTheme.Dark ? 57 : 10;
        if (Math.Abs(footer.ActualWidth - window.PanelRoot.ActualWidth) > 1 || footer.Background is null
            || Math.Abs(footer.ActualHeight - 50) > 0.1
            || footer.Background is not SolidColorBrush { Color.A: > 0 and < 64 }
            || ((SolidColorBrush)footer.Background).Color.A != expectedFooterAlpha
            || footer.BorderBrush is not SolidColorBrush { Color: { R: 0, G: 0, B: 0, A: > 0 and < 128 } }
            || ((SolidColorBrush)footer.BorderBrush).Color.A != expectedDividerAlpha
            || stroke.BorderBrush is not SolidColorBrush { Color.A: > 0 and < 32 }
            || surface.Shadow is not null || surface.Translation != System.Numerics.Vector3.Zero
            || footer.BorderThickness.Top != 1 || footer.Child is not Grid footerContent
            || !footerContent.Children.Contains(refresh) || refresh.Content is not FontIcon { FontSize: 14 }
            || refresh.ActualWidth != 36 || refresh.ActualHeight != 36)
            throw new InvalidOperationException("The flyout did not have a full-width footer with an icon-only refresh button.");
        var expectedStroke = window.Root.ActualTheme == ElementTheme.Dark
            ? global::Windows.UI.Color.FromArgb(24, 255, 255, 255)
            : global::Windows.UI.Color.FromArgb(20, 0, 0, 0);
        foreach (var card in Descendants(window.Root).OfType<Border>().Where(border => border.Name == "DisplayCard"))
            if (card.BorderBrush is not SolidColorBrush brush || brush.Color != expectedStroke || card.BorderThickness != new Thickness(1))
                throw new InvalidOperationException("Display card strokes do not follow the native light/dark theme.");
    }

    private static async Task RequireRefreshStatusDoesNotMoveCardsAsync(FlyoutWindow window,
        DemoDisplayDiscovery discovery, string directory)
    {
        var cards = Descendants(window.Root).OfType<Border>().Where(border => border.Name == "DisplayCard").ToArray();
        var before = cards.Select(card => card.TransformToVisual(window.Root)
            .TransformPoint(new global::Windows.Foundation.Point())).ToArray();
        var size = window.AppWindow.Size;
        var footer = (Border)window.Root.FindName("FooterPanel");
        var lastCard = cards[^1];
        var gap = footer.TransformToVisual(window.Root).TransformPoint(new global::Windows.Foundation.Point()).Y
            - before[^1].Y - lastCard.ActualHeight;
        if (gap < 12 || gap > 24.1)
            throw new InvalidOperationException($"The last display card has an excessive footer gap: {gap}.");
        using var gate = new ManualResetEventSlim();
        discovery.DiscoveryGate = gate;
        var refresh = window.RefreshOrShowAsync();
        try
        {
            await Task.Delay(80);
            window.Root.UpdateLayout();
            var status = (TextBlock)window.Root.FindName("FooterStatus");
            if (!window.ViewModel.IsRefreshing || status.Text != "Finding displays…"
                || window.AppWindow.Size != size || !Descendants(footer).Contains(status))
                throw new InvalidOperationException("Refresh did not show its status inside the fixed-height footer.");
            for (var index = 0; index < cards.Length; index++)
            {
                var current = cards[index].TransformToVisual(window.Root).TransformPoint(new global::Windows.Foundation.Point());
                if (Math.Abs(current.Y - before[index].Y) > 0.1 || Math.Abs(current.X - before[index].X) > 0.1)
                    throw new InvalidOperationException("Refresh status moved the display cards.");
            }
            // Layout preview includes the actual busy status, without a hardware discovery.
            window.PanelRoot.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 32, 32, 32));
            await CaptureAsync(window.Root, directory, "winui-refreshing.png");
        }
        finally
        {
            gate.Set();
            discovery.DiscoveryGate = null;
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            window.PanelRoot.Background = null;
        }
    }

    private static void RequireNoNativeFrame(FlyoutWindow window)
    {
        if (!NativeWindowPosition.ClientFillsWindow(window.WindowHandle))
            throw new InvalidOperationException($"A native frame still separates the flyout's client and window bounds. {NativeWindowPosition.DescribeFrame(window.WindowHandle)}");
    }

    private static void RequireSliderThumbDiameter(IEnumerable<Slider> sliders)
    {
        foreach (var slider in sliders)
        {
            var thumb = Descendants(slider).OfType<Microsoft.UI.Xaml.Controls.Primitives.Thumb>()
                .Single(element => element.Name == "HorizontalThumb");
            var outer = Descendants(thumb).OfType<Border>().First();
            if (Math.Abs(outer.ActualWidth - 20) > 0.1 || Math.Abs(outer.ActualHeight - 20) > 0.1)
                throw new InvalidOperationException($"The slider's outer thumb is {outer.ActualWidth}×{outer.ActualHeight}, expected 20×20 DIP.");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static async Task CaptureAsync(UIElement root, string directory, string name)
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

// Native WinUI can handle a projected callback exception while Visual Studio still breaks on it.
internal sealed class BackdropCallbackErrorTracker : IDisposable
{
    private int _count;
    private string? _firstFailure;
    internal int Count => Volatile.Read(ref _count);

    internal BackdropCallbackErrorTracker() => AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;

    private void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs eventArgs)
    {
        var exception = eventArgs.Exception;
        if (exception is not ArgumentException || exception.HResult != unchecked((int)0x80070057)) return;
        var stack = exception.StackTrace;
        if (stack is null || !stack.Contains(nameof(FlyoutAcrylicBackdrop), StringComparison.Ordinal)
            && !stack.Contains("ISystemBackdropOverrides", StringComparison.Ordinal)) return;
        Interlocked.Increment(ref _count);
        Interlocked.CompareExchange(ref _firstFailure, exception.ToString(), null);
    }

    internal void ThrowIfAny()
    {
        if (Count != 0)
            throw new InvalidOperationException($"The acrylic backdrop raised {Count} invalid-argument callback exceptions. First exception:{Environment.NewLine}{_firstFailure}");
    }

    public void Dispose() => AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
}
