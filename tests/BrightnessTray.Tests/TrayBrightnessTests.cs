using BrightnessTray.App.ViewModels;
using BrightnessTray.Core;
using BrightnessTray.Core.Displays;

internal static class TrayBrightnessTests
{
    internal static Task FixedStepAsync() => UiThreadContext.RunAsync(async () =>
    {
        var tracker = new NativeOperationTracker();
        var low = 40;
        var high = 80;
        var discovery = new FakeDiscovery(tracker, () => new([
            new FakeDevice("low", tracker) { Read = () => low, Set = value => low = value },
            new FakeDevice("high", tracker) { Read = () => high, Set = value => high = value }], []));
        await using var service = new BrightnessService(discovery);
        await using var flyout = new FlyoutViewModel(service);
        await using var wheel = new TrayBrightnessController(flyout);
        await wheel.ScrollAsync(120);
        await DrainAsync(flyout);
        Equal(45, low);
        Equal(85, high);
        await wheel.ScrollAsync(-120);
        await DrainAsync(flyout);
        Equal(40, low);
        Equal(80, high);
        Equal(1, discovery.DiscoverCount);

        low = 20;
        high = 60;
        await Task.Delay(1100);
        await wheel.ScrollAsync(120);
        await DrainAsync(flyout);
        Equal(25, low);
        Equal(65, high);
        Equal(2, discovery.DiscoverCount);
    });

    internal static Task FineWheelAndLimitsAsync() => UiThreadContext.RunAsync(async () =>
    {
        var tracker = new NativeOperationTracker();
        var low = 1;
        var high = 99;
        var zero = 0;
        await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([
            new FakeDevice("low", tracker) { Read = () => low, Set = value => low = value },
            new FakeDevice("high", tracker) { Read = () => high, Set = value => high = value },
            new FakeDevice("zero", tracker) { Read = () => zero, Set = value => zero = value }], [])));
        await using var flyout = new FlyoutViewModel(service);
        await using var wheel = new TrayBrightnessController(flyout);
        await wheel.ScrollAsync(60);
        Equal(1d, flyout.Displays[0].Brightness);
        Equal(0, low + zero - 1);
        await wheel.ScrollAsync(60);
        Equal(6d, flyout.Displays[0].Brightness);
        Equal(100d, flyout.Displays[1].Brightness);
        Equal(5d, flyout.Displays[2].Brightness);
        for (var index = 0; index < 19; index++) await wheel.ScrollAsync(120);
        await DrainAsync(flyout);
        Equal(100, low);
        Equal(100, high);
        Equal(100, zero);
        await wheel.ScrollAsync(-120);
        await DrainAsync(flyout);
        Equal(95, low);
        Equal(95, high);
        Equal(95, zero);
        await wheel.ScrollAsync(-120000);
        await DrainAsync(flyout);
        Equal(0, low);
        Equal(0, high);
        await wheel.ScrollAsync(120000);
        await DrainAsync(flyout);
        Equal(100, low);
        Equal(100, high);
    });

    internal static Task UnavailableAndShutdownAsync() => UiThreadContext.RunAsync(async () =>
    {
        var tracker = new NativeOperationTracker();
        var unavailable = new FakeDevice("off", tracker) { Read = () => throw new IOException("Off") };
        var failing = new FakeDevice("lost", tracker) { Read = () => 50, Set = _ => throw new IOException("Disconnected") };
        var good = new FakeDevice("good", tracker) { Read = () => 60 };
        await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([unavailable, failing, good], [])));
        await using var flyout = new FlyoutViewModel(service);
        await using var wheel = new TrayBrightnessController(flyout);
        await wheel.ScrollAsync(240);
        await DrainAsync(flyout);
        Equal(0, unavailable.SetCount);
        Equal(1, failing.SetCount);
        Equal(1, good.SetCount);
        Equal(1, flyout.Displays.Count);
        Equal("good", flyout.Displays.Single().Id);
        Equal(70d, flyout.Displays.Single().Brightness);
        await wheel.DisposeAsync();
        await wheel.ScrollAsync(120);
        Equal(1, good.SetCount);
    });

    internal static Task SlowAndCoarseAsync() => UiThreadContext.RunAsync(async () =>
    {
        var tracker = new NativeOperationTracker();
        var low = 9;
        var coarse = 50;
        await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([
            new FakeDevice("low", tracker) { Read = () => low, Set = value => low = value },
            new FakeDevice("coarse", tracker) { Read = () => coarse, Set = value => coarse = value / 10 * 10 }], [])));
        await using var flyout = new FlyoutViewModel(service);
        await using var wheel = new TrayBrightnessController(flyout);
        await wheel.ScrollAsync(120);
        await DrainAsync(flyout);
        Equal(14, low);
        Equal(50, coarse);
        await Task.Delay(1100);
        await wheel.ScrollAsync(120);
        await DrainAsync(flyout);
        Equal(19, low);
        Equal(60, coarse);

        flyout.Displays[0].Brightness = 20;
        await DrainAsync(flyout);
        await wheel.ScrollAsync(120);
        await DrainAsync(flyout);
        Equal(25, low);
    });

    internal static Task ReentrantRefreshAsync() => UiThreadContext.RunAsync(async () =>
    {
        var tracker = new NativeOperationTracker();
        var first = 40;
        var second = 80;
        await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([
            new FakeDevice("first", tracker) { Read = () => first, Set = value => first = value },
            new FakeDevice("second", tracker) { Read = () => second, Set = value => second = value }], [])));
        await using var flyout = new FlyoutViewModel(service);
        await flyout.RefreshAsync();
        await using var wheel = new TrayBrightnessController(flyout);
        await wheel.ScrollAsync(60);
        Task? refresh = null;
        flyout.Displays[0].PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(DisplayViewModel.Brightness) && refresh is null) refresh = flyout.RefreshAsync();
        };
        await wheel.ScrollAsync(60);
        if (refresh is not null) await refresh;
        await DrainAsync(flyout);
        Equal(45, first);
        Equal(85, second);
    });

    internal static Task VisiblePanelSlowWheelAsync() => UiThreadContext.RunAsync(async () =>
    {
        var tracker = new NativeOperationTracker();
        var low = 40;
        var high = 80;
        var opened = new List<FakeDevice>();
        var discovery = new FakeDiscovery(tracker, () =>
        {
            var first = new FakeDevice("low", tracker) { Read = () => low, Set = value => low = value };
            var second = new FakeDevice("high", tracker) { Read = () => high, Set = value => high = value };
            opened.AddRange([first, second]);
            return new([first, second], []);
        });
        await using var service = new BrightnessService(discovery);
        await using var flyout = new FlyoutViewModel(service);
        await flyout.RefreshAsync();
        var rows = flyout.Displays.ToArray();
        var canRefresh = false;
        var refreshNotifications = 0;
        flyout.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FlyoutViewModel.IsRefreshing)) refreshNotifications++;
        };
        var text = string.Empty;
        await using var tooltip = new TrayBrightnessToolTip(flyout, value => text = value, () => canRefresh);
        await using var wheel = new TrayBrightnessController(flyout, () => canRefresh);
        await wheel.ScrollAsync(120);
        Equal(true, rows.All(row => row.IsAvailable));
        Equal("Display low: 45%\nDisplay high: 85%", text);
        await DrainAsync(flyout);
        await Task.Delay(1100);
        await wheel.ScrollAsync(120);
        Equal(true, rows.All(row => row.IsAvailable));
        Equal("Display low: 50%\nDisplay high: 90%", text);
        await DrainAsync(flyout);
        Equal(50, low);
        Equal(90, high);
        Equal(1, discovery.DiscoverCount);
        Equal(0, refreshNotifications);
        Equal(false, flyout.IsRefreshing);
        Equal(true, rows.Select((row, index) => ReferenceEquals(row, flyout.Displays[index])).All(same => same));
        Equal(true, opened.All(device => device.ReadCount == 1 && device.DisposeCount == 0));

        // Once hidden, the next gesture again reads externally changed hardware values.
        await Task.Delay(1100);
        low = 20;
        high = 60;
        canRefresh = true;
        await wheel.ScrollAsync(120);
        await DrainAsync(flyout);
        Equal(25, low);
        Equal(65, high);
        Equal(2, discovery.DiscoverCount);
        Equal(2, refreshNotifications);
        Equal("Display low: 25%\nDisplay high: 65%", text);
    });

    private static async Task DrainAsync(FlyoutViewModel flyout)
    {
        var rows = flyout.Displays.ToArray();
        await Task.WhenAll(rows.Select(row => row.PauseAndDrainAsync()));
        foreach (var row in rows) row.Resume();
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
