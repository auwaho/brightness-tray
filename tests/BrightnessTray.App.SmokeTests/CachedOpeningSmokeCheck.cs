using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BrightnessTray.App.ViewModels;
using BrightnessTray.Core;
using BrightnessTray.Core.Displays;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BrightnessTray.App;

// Every delayed operation belongs to a virtual display. Gates prove ordering without timing the hardware.
internal static class CachedOpeningSmokeCheck
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync(string directory)
    {
        using var errors = new BackdropCallbackErrorTracker();
        using var discovery = new GatedDiscovery();
        await using var service = new BrightnessService(discovery);
        var model = new FlyoutViewModel(service);
        var window = new FlyoutWindow(model, requireForeground: false);
        try
        {
            var cold = discovery.PrepareDiscovery(2);
            var opening = window.ShowNearTrayAsync();
            await cold.Started.Task.WaitAsync(Deadline);
            await opening.WaitAsync(Deadline);
            Require(window.IsVisible && model.IsRefreshing && model.Displays.Count == 0 && !cold.IsReleased,
                "Cold opening waited for discovery instead of displaying the loading panel.");
            Require(!((Button)window.Root.FindName("RefreshButton")).IsEnabled,
                "The loading panel allowed a second refresh.");
            cold.Release();
            await model.RefreshAsync().WaitAsync(Deadline);
            await SettleAsync(window);
            Require(model.Displays.Count == 2 && Sliders(window).Length == 2,
                "Cold discovery did not populate the already-visible panel.");

            // Let fast discovery race the reveal/layout work instead of keeping it blocked until after opening.
            window.Hide();
            var fast = discovery.PrepareDiscovery(3);
            opening = window.ShowNearTrayAsync();
            await fast.Started.Task.WaitAsync(Deadline);
            fast.Release();
            await Task.WhenAll(opening, model.RefreshAsync()).WaitAsync(Deadline);
            await SettleAsync(window);
            Require(window.IsVisible && model.Displays.Count == 3 && Sliders(window).Length == 3,
                "A refresh completing around opening left the panel at an intermediate layout.");
            var fastHeight = window.AppWindow.Size.Height;
            window.Hide();
            await window.ShowNearTrayAsync().WaitAsync(Deadline);
            await model.RefreshAsync().WaitAsync(Deadline);
            await SettleAsync(window);
            Require(window.AppWindow.Size.Height == fastHeight,
                "The final opening layout missed a refresh completed during preparation or reveal.");

            window.Hide();
            var rows = model.Displays.ToArray();
            var cached = discovery.PrepareDiscovery(4);
            opening = window.ShowNearTrayAsync();
            await cached.Started.Task.WaitAsync(Deadline);
            await opening.WaitAsync(Deadline);
            RequireCache(window, rows);
            var cachedHeight = window.AppWindow.Size.Height;
            var anchoredBottom = window.AppWindow.Position.Y + cachedHeight;
            var pending = model.RefreshAsync();
            var discoveryCount = discovery.DiscoveryCount;
            window.Hide();
            await window.ShowNearTrayAsync().WaitAsync(Deadline);
            Require(ReferenceEquals(pending, model.RefreshAsync()) && discovery.DiscoveryCount == discoveryCount,
                "Reopening started duplicate discovery instead of joining the pending refresh.");
            RequireCache(window, rows);
            cached.Release();
            await pending.WaitAsync(Deadline);
            await SettleAsync(window);
            Require(model.Displays.Count == 4 && Sliders(window).Length == 4,
                "Late discovery did not replace the cached rows.");
            Require(window.AppWindow.Size.Height > cachedHeight
                && window.AppWindow.Position.Y + window.AppWindow.Size.Height == anchoredBottom,
                "Late discovery did not resize the panel while retaining its bottom anchor.");

            // An in-flight write must finish before discovery, but must not hold the panel concealed.
            window.Hide();
            var write = discovery.PrepareWrite();
            model.Displays[0].Brightness = 66;
            await write.Started.Task.WaitAsync(Deadline);
            var afterWrite = discovery.PrepareDiscovery(4);
            discoveryCount = discovery.DiscoveryCount;
            rows = model.Displays.ToArray();
            opening = window.ShowNearTrayAsync();
            await opening.WaitAsync(Deadline);
            RequireCache(window, rows);
            Require(!afterWrite.Started.Task.IsCompleted && discovery.DiscoveryCount == discoveryCount,
                "Discovery released or replaced devices before the pending write finished.");
            write.Release();
            await afterWrite.Started.Task.WaitAsync(Deadline);
            Require(discovery.WriteCount == 1, "The user's pending brightness change was lost.");
            afterWrite.Release();
            await model.RefreshAsync().WaitAsync(Deadline);
            await SettleAsync(window);
            Require(model.Displays[0].Brightness == 66, "Refresh lost the drained brightness value.");

            window.Hide();
            var hidden = discovery.PrepareDiscovery(2);
            opening = window.ShowNearTrayAsync();
            await hidden.Started.Task.WaitAsync(Deadline);
            await opening.WaitAsync(Deadline);
            await window.ToggleFromTrayAsync().WaitAsync(Deadline);
            var hiddenPosition = window.AppWindow.Position;
            var hiddenSize = window.AppWindow.Size;
            hidden.Release();
            await model.RefreshAsync().WaitAsync(Deadline);
            await SettleAsync(window);
            Require(!window.IsVisible && window.AppWindow.Position == hiddenPosition && window.AppWindow.Size == hiddenSize,
                "Completing discovery reopened or moved a dismissed panel.");

            var menu = discovery.PrepareDiscovery(3);
            opening = window.ShowNearTrayAsync();
            await menu.Started.Task.WaitAsync(Deadline);
            await opening.WaitAsync(Deadline);
            await window.ShowTrayMenuAsync().WaitAsync(Deadline);
            var menuPosition = window.AppWindow.Position;
            var menuSize = window.AppWindow.Size;
            menu.Release();
            await model.RefreshAsync().WaitAsync(Deadline);
            await SettleAsync(window);
            Require(window.IsTrayMenuVisible && window.Root.ActualWidth == 248
                && window.AppWindow.Position == menuPosition && window.AppWindow.Size == menuSize,
                "Completing display discovery replaced or resized the tray menu.");

            window.Hide();
            rows = model.Displays.ToArray();
            var failure = discovery.PrepareDiscovery(2, fail: true);
            opening = window.ShowNearTrayAsync();
            await failure.Started.Task.WaitAsync(Deadline);
            await opening.WaitAsync(Deadline);
            RequireCache(window, rows);
            failure.Release();
            await model.RefreshAsync().WaitAsync(Deadline);
            await SettleAsync(window);
            Require(model.Displays.SequenceEqual(rows) && rows.All(row => row.IsAvailable)
                && Sliders(window).All(slider => slider.IsEnabled)
                && model.Status.StartsWith("Could not find displays:", StringComparison.Ordinal),
                "Discovery failure discarded the cache or left its controls paused.");

            window.Hide();
            var shutdown = discovery.PrepareDiscovery(2);
            opening = window.ShowNearTrayAsync();
            await shutdown.Started.Task.WaitAsync(Deadline);
            await opening.WaitAsync(Deadline);
            window.Hide();
            var disposal = model.DisposeAsync().AsTask();
            Require(!disposal.IsCompleted && !shutdown.IsReleased,
                "Shutdown returned before the pending discovery finished.");
            shutdown.Release();
            await disposal.WaitAsync(Deadline);
            await SettleAsync(window);
            Require(!window.IsVisible && rows.All(row => !row.IsAvailable),
                "Shutdown resumed stale rows or reopened the flyout.");
            errors.ThrowIfAny();
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            {
                success = true,
                coldOpeningBeforeDiscovery = true,
                fastRefreshDuringOpening = true,
                cachedRowsVisibleAndPaused = true,
                reopeningJoinsPendingRefresh = true,
                lateRefreshRetainsBottomAnchor = true,
                writesDrainedWithoutBlockingReveal = true,
                dismissedPanelStaysHidden = true,
                trayMenuUnaffected = true,
                failedDiscoveryResumesCache = true,
                shutdownDrainsRefresh = true,
                virtualWriteCount = discovery.WriteCount,
                hardwareBrightnessChanged = false,
                windowsSettingsChanged = false,
                backdropCallbackErrors = errors.Count,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            discovery.ReleaseAll();
            window.Hide();
            await model.DisposeAsync().AsTask().WaitAsync(Deadline);
            window.CloseForExit();
        }
    }

    private static void RequireCache(FlyoutWindow window, DisplayViewModel[] rows)
    {
        Require(window.IsVisible && window.ViewModel.IsRefreshing
            && window.ViewModel.Displays.SequenceEqual(rows) && rows.All(row => !row.IsAvailable)
            && Sliders(window).Length == rows.Length && Sliders(window).All(slider => !slider.IsEnabled),
            "Cached rows were removed, replaced, or left editable while discovery was pending.");
    }

    private static async Task SettleAsync(FlyoutWindow window)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Require(window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => settled.TrySetResult()),
            "The test could not drain the UI queue.");
        await settled.Task.WaitAsync(Deadline);
        window.Root.UpdateLayout();
    }

    private static Slider[] Sliders(FlyoutWindow window) => Descendants(window.Root).OfType<Slider>().ToArray();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Gate(int count = 0, bool fail = false) : IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsReleased => _release.IsSet;
        internal int Count => count;
        internal void Wait()
        {
            Started.TrySetResult();
            _release.Wait();
            if (fail) throw new InvalidOperationException("Simulated display discovery failure.");
        }
        internal void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class GatedDiscovery : IDisplayDiscovery, IDisposable
    {
        private readonly List<Gate> _gates = [];
        private readonly int[] _values = [65, 80, 50, 35];
        private Gate? _nextDiscovery;
        private Gate? _nextWrite;
        private int _count = 2;
        private int _discoveryCount;
        private int _writeCount;
        internal int DiscoveryCount => Volatile.Read(ref _discoveryCount);
        internal int WriteCount => Volatile.Read(ref _writeCount);

        internal Gate PrepareDiscovery(int count, bool fail = false)
        {
            var gate = new Gate(count, fail);
            _gates.Add(gate);
            Require(Interlocked.CompareExchange(ref _nextDiscovery, gate, null) is null,
                "The test already has an unconsumed discovery gate.");
            return gate;
        }

        internal Gate PrepareWrite()
        {
            var gate = new Gate();
            _gates.Add(gate);
            Require(Interlocked.CompareExchange(ref _nextWrite, gate, null) is null,
                "The test already has an unconsumed write gate.");
            return gate;
        }

        public DisplayDiscoveryResult Discover()
        {
            Interlocked.Increment(ref _discoveryCount);
            var gate = Interlocked.Exchange(ref _nextDiscovery, null);
            gate?.Wait();
            if (gate is not null) _count = gate.Count;
            return new(Enumerable.Range(0, _count).Select(index => (IBrightnessDevice)new Device(this, index)).ToArray(), []);
        }

        internal void ReleaseAll() { foreach (var gate in _gates) gate.Release(); }
        public void Dispose() { foreach (var gate in _gates) gate.Dispose(); }

        private sealed class Device(GatedDiscovery owner, int index) : IBrightnessDevice
        {
            private bool _disposed;
            public string Id => $"cached-opening-{index}";
            public string Name => $"Virtual display {index + 1}";
            public string ConnectionLabel => "Virtual";
            public int ReadBrightness() => owner._values[index];
            public int SetBrightness(int percent)
            {
                var gate = Interlocked.Exchange(ref owner._nextWrite, null);
                gate?.Wait();
                Require(!_disposed, "A pending write used a disposed display handle.");
                Interlocked.Increment(ref owner._writeCount);
                return owner._values[index] = percent;
            }
            public void Dispose() => _disposed = true;
        }
    }
}
