using BrightnessTray.App.ViewModels;
using BrightnessTray.Core;

internal static class TrayBrightnessToolTipTests
{
    internal static Task ValuesAndRecoveryAsync() => UiThreadContext.RunAsync(async () =>
    {
        var tracker = new NativeOperationTracker();
        var low = 40;
        var high = 80;
        var lost = false;
        await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([
            new FakeDevice("low", tracker) { Read = () => low, Set = value => low = value },
            new FakeDevice("high", tracker)
            {
                Read = () => lost ? throw new IOException("Off") : high,
                Set = value => lost ? throw new IOException("Disconnected") : high = value / 10 * 10,
            }], [])));
        await using var flyout = new FlyoutViewModel(service);
        var text = string.Empty;
        var updates = new List<string>();
        await using var tooltip = new TrayBrightnessToolTip(flyout, value => { text = value; updates.Add(value); });
        Equal("BrightnessTray · Display brightness", text);
        await tooltip.RefreshOnHoverAsync();
        Equal("Display low: 40%\nDisplay high: 80%", text);
        await using var wheel = new TrayBrightnessController(flyout);
        updates.Clear();
        await wheel.ScrollAsync(120);
        Equal("Display low: 45%\nDisplay high: 85%", text);
        Require(updates.All(update => update.Contains("Display low", StringComparison.Ordinal)),
            "Replacing rows briefly cleared the native tooltip.");
        await DrainAsync(flyout);
        Equal("Display low: 45%\nDisplay high: 80%", text);
        lost = true;
        await wheel.ScrollAsync(120);
        await DrainAsync(flyout);
        Equal("Display low: 50%", text);
        lost = false;
        await flyout.RefreshAsync();
        Equal("Display low: 50%\nDisplay high: 80%", text);
        await tooltip.DisposeAsync();
        var updateCount = updates.Count;
        flyout.Displays[0].Brightness = 60;
        await DrainAsync(flyout);
        Equal(updateCount, updates.Count);
    });

    internal static Task FormatAsync()
    {
        var longName = string.Concat(Enumerable.Repeat("🖥️Monitor", 25));
        var text = TrayBrightnessToolTip.Format([(longName, 45), (longName + " second", 85)]);
        Require(text.Length <= 127 && text.Contains(": 45%\n", StringComparison.Ordinal)
            && text.EndsWith(": 85%", StringComparison.Ordinal), "Long display names lost brightness values.");
        Require(text.Contains('…'), "Long names were not abbreviated.");
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                Require(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]), "A name ended in a split surrogate pair.");
                i++;
            }
            else Require(!char.IsLowSurrogate(text[i]), "A name began with an unpaired low surrogate.");
        }
        var many = TrayBrightnessToolTip.Format(Enumerable.Range(1, 12).Select(i => ($"Display {i}", i)).ToArray());
        Require(many.Length <= 127 && many.EndsWith("+6 more displays", StringComparison.Ordinal)
            && many.Contains("Display 1: 1%", StringComparison.Ordinal), "Many-display summary exceeded native limits.");
        Equal("First  Display: 100%\nDisplay: 0%", TrayBrightnessToolTip.Format([("First\n\0Display", 101), ("\r\n", -1)]));
        Equal("No controllable displays found.", TrayBrightnessToolTip.Format([]));
        return Task.CompletedTask;
    }

    internal static Task HoverRefreshAsync() => UiThreadContext.RunAsync(async () =>
    {
        var tracker = new NativeOperationTracker();
        var brightness = 40;
        var available = true;
        var discovery = new FakeDiscovery(tracker, () => new([
            new FakeDevice("panel", tracker) { Read = () => available ? brightness : throw new IOException("Off") }], []));
        await using var service = new BrightnessService(discovery);
        await using var flyout = new FlyoutViewModel(service);
        var text = string.Empty;
        await using var tooltip = new TrayBrightnessToolTip(flyout, value => text = value);
        await tooltip.RefreshOnHoverAsync();
        Equal("Display panel: 40%", text);
        brightness = 25;
        await tooltip.RefreshOnHoverAsync();
        Equal(1, discovery.DiscoverCount);
        Equal("Display panel: 40%", text);
        await Task.Delay(1100);
        await tooltip.RefreshOnHoverAsync();
        Equal(2, discovery.DiscoverCount);
        Equal("Display panel: 25%", text);
        available = false;
        await flyout.RefreshAsync();
        Equal("No controllable displays found.", text);
        available = true;
        await flyout.RefreshAsync();
        Equal("Display panel: 25%", text);
    });

    internal static Task HoverConcurrencyAndDisposeAsync() => UiThreadContext.RunAsync(async () =>
    {
        var tracker = new NativeOperationTracker();
        using var blocked = new BlockingNativeOperation();
        var discovery = new FakeDiscovery(tracker, () =>
        {
            blocked.Execute();
            return new([new FakeDevice("panel", tracker)], []);
        });
        await using var service = new BrightnessService(discovery);
        await using var flyout = new FlyoutViewModel(service);
        var updates = new List<string>();
        await using var tooltip = new TrayBrightnessToolTip(flyout, updates.Add);
        var first = tooltip.RefreshOnHoverAsync();
        await blocked.Entered;
        var second = tooltip.RefreshOnHoverAsync();
        Require(ReferenceEquals(first, second), "Concurrent hover requests did not join their published refresh.");
        var disposal = tooltip.DisposeAsync().AsTask();
        Require(!disposal.IsCompleted, "Tooltip disposal returned while discovery still owned native work.");
        blocked.Release();
        await Task.WhenAll(first, second, disposal);
        Equal(1, discovery.DiscoverCount);
        Equal(1, updates.Count);
        await tooltip.RefreshOnHoverAsync();
        Equal(1, discovery.DiscoverCount);
        await tooltip.DisposeAsync();
    });

    internal static Task VisiblePanelHoverAsync() => UiThreadContext.RunAsync(async () =>
    {
        var tracker = new NativeOperationTracker();
        var brightness = 40;
        var opened = new List<FakeDevice>();
        var discovery = new FakeDiscovery(tracker, () =>
        {
            var device = new FakeDevice("panel", tracker) { Read = () => brightness, Set = value => brightness = value };
            opened.Add(device);
            return new([device], []);
        });
        await using var service = new BrightnessService(discovery);
        await using var flyout = new FlyoutViewModel(service);
        await flyout.RefreshAsync();
        var row = flyout.Displays.Single();
        var canRefresh = false;
        var refreshNotifications = 0;
        flyout.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FlyoutViewModel.IsRefreshing)) refreshNotifications++;
        };
        var text = string.Empty;
        await using var tooltip = new TrayBrightnessToolTip(flyout, value => text = value, () => canRefresh);
        await tooltip.RefreshOnHoverAsync();
        await Task.Delay(1100);
        await tooltip.RefreshOnHoverAsync();
        Equal(1, discovery.DiscoverCount);
        Equal(1, opened[0].ReadCount);
        Equal(0, opened[0].DisposeCount);
        Equal(0, refreshNotifications);
        Equal(false, flyout.IsRefreshing);
        Equal(true, row.IsAvailable);
        Require(ReferenceEquals(row, flyout.Displays.Single()), "Hover replaced the visible display row.");

        row.Brightness = 55;
        Equal("Display panel: 55%", text);
        await DrainAsync(flyout);
        Equal(55, brightness);
        Equal("Display panel: 55%", text);
        // Skipped hovers must not consume the cooldown for a subsequent genuine hidden-panel entry.
        brightness = 25;
        canRefresh = true;
        await tooltip.RefreshOnHoverAsync();
        Equal(2, discovery.DiscoverCount);
        Equal(2, refreshNotifications);
        Equal("Display panel: 25%", text);
        Require(!ReferenceEquals(row, flyout.Displays.Single()), "Hidden-panel hover did not refresh devices.");
    });

    internal static Task VisibleHoverDuringWriteAsync() => UiThreadContext.RunAsync(async () =>
    {
        var tracker = new NativeOperationTracker();
        using var blocked = new BlockingNativeOperation();
        var device = new FakeDevice("panel", tracker) { Set = value => { blocked.Execute(); return value; } };
        var discovery = new FakeDiscovery(tracker, () => new([device], []));
        await using var service = new BrightnessService(discovery);
        await using var flyout = new FlyoutViewModel(service);
        await flyout.RefreshAsync();
        var row = flyout.Displays.Single();
        var text = string.Empty;
        await using var tooltip = new TrayBrightnessToolTip(flyout, value => text = value, () => false);
        row.Brightness = 60;
        try
        {
            await blocked.Entered;
            var hover = tooltip.RefreshOnHoverAsync();
            Require(hover.IsCompleted, "Visible-panel hover waited for an in-flight brightness write.");
            await hover;
            Equal(1, discovery.DiscoverCount);
            Equal(1, device.ReadCount);
            Equal(false, flyout.IsRefreshing);
            Equal(true, row.IsAvailable);
            row.Brightness = 75;
            Equal("Display panel: 75%", text);
            var dispose = tooltip.DisposeAsync().AsTask();
            Require(dispose.IsCompleted, "Tooltip disposal waited for a refresh that should have been skipped.");
            await dispose;
        }
        finally { blocked.Release(); }
        await DrainAsync(flyout);
        Equal(75d, row.Brightness);
        Equal(2, device.SetCount);
        Equal(0, device.DisposeCount);
    });

    private static async Task DrainAsync(FlyoutViewModel flyout)
    {
        var rows = flyout.Displays.ToArray();
        await Task.WhenAll(rows.Select(row => row.PauseAndDrainAsync()));
        foreach (var row in rows) row.Resume();
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
