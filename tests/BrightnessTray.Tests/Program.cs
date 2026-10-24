using System.Collections.Concurrent;
using BrightnessTray.App.ViewModels;
using BrightnessTray.Core;
using BrightnessTray.Core.Displays;
using BrightnessTray.Windows;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Flyout opening and closing use the production linear interpolation", FlyoutAnimationTests.LinearInterpolationAsync),
    ("Sparse animation frames remain valid without a midpoint sample", FlyoutAnimationTests.SparseFramesAsync),
    ("DDC retry re-establishes the VCP range before writing a scaled value", DdcBrightnessTests.TransientFallbackRangeAsync),
    ("Failed DDC fallback never writes with the previous API range", DdcBrightnessTests.FailedFallbackNeverWritesAsync),
    ("DDC fallback commits its mode only with a valid range", DdcBrightnessTests.ReadFallbackAndInvalidRangeAsync),
    ("Tray hover refresh fires once despite transient Shell hit-test failures", TrayIconHoverTests.StableEntryAsync),
    ("Visible-panel tray hover keeps existing rows editable and tooltip values live", TrayBrightnessToolTipTests.VisiblePanelHoverAsync),
    ("Visible-panel hover and tooltip disposal do not wait for pending native writes", TrayBrightnessToolTipTests.VisibleHoverDuringWriteAsync),
    ("Slow tray scrolling uses visible rows without repeated discovery or pauses", TrayBrightnessTests.VisiblePanelSlowWheelAsync),
    ("Only recognized native transport, busy and timeout errors are transient", RetryAndDiagnosticsTests.ClassificationAsync),
    ("Brightness reads and absolute writes recover within two retries and report recovery", RetryAndDiagnosticsTests.RecoveryAsync),
    ("Retry budgets preserve original errors and never retry permanent failures", RetryAndDiagnosticsTests.BudgetAndPermanentErrorsAsync),
    ("New slider input survives a retry while refresh or exit waits for hardware work", RetryAndDiagnosticsTests.PendingInputAndLifetimeAsync),
    ("Exhausted retries isolate displays and record final read, write and discovery failures", RetryAndDiagnosticsTests.FinalFailuresAsync),
    ("Diagnostics rotate within size limits and tolerate concurrent writes and unavailable storage", RetryAndDiagnosticsTests.RotationAndStorageFailureAsync),
    ("Slider wheel changes only its display, accumulates fine input, clamps and freezes during refresh", SliderWheelTests.InputAndLimitsAsync),
    ("Slider wheel preserves progress on coarse drivers and stops after a write failure", SliderWheelTests.CoarseAndFailureAsync),
    ("Native tray tooltip follows requested, confirmed, unavailable and recovered displays", TrayBrightnessToolTipTests.ValuesAndRecoveryAsync),
    ("Native tray tooltip bounds long and many names without splitting Unicode or dropping percentages", TrayBrightnessToolTipTests.FormatAsync),
    ("Native tray hover refresh is throttled and discovers external brightness changes", TrayBrightnessToolTipTests.HoverRefreshAsync),
    ("Native tray hover joins refresh and disposal waits without publishing stale text", TrayBrightnessToolTipTests.HoverConcurrencyAndDisposeAsync),
    ("Tray wheel changes all displays by five percentage points and re-reads after idle", TrayBrightnessTests.FixedStepAsync),
    ("Tray wheel accumulates fine deltas and clamps endpoints", TrayBrightnessTests.FineWheelAndLimitsAsync),
    ("Tray wheel isolates unavailable displays and stops on shutdown", TrayBrightnessTests.UnavailableAndShutdownAsync),
    ("Tray wheel preserves progress across idle and coarse driver levels", TrayBrightnessTests.SlowAndCoarseAsync),
    ("Tray wheel reaches every display across a reentrant refresh", TrayBrightnessTests.ReentrantRefreshAsync),
    ("WMI void providers succeed without hiding missing statuses or driver errors", WindowsBackendTests.WmiResultsAsync),
    ("Mixed mirrored endpoints map without losing external or disabled displays", WindowsBackendTests.EndpointMappingAsync),
    ("A real driver rejection hides only its row and refresh can recover it", () => UiThreadContext.RunAsync(RejectedWriteRecoveryAsync)),
    ("A failed last display is hidden and exit still drains its worker", () => UiThreadContext.RunAsync(LastDisplayWriteFailureAsync)),
    ("Failed discovery does not restore a row hidden by a write failure", () => UiThreadContext.RunAsync(FailedRefreshKeepsFailedRowHiddenAsync)),
    ("A rollback listener cannot queue another write after a driver failure", () => UiThreadContext.RunAsync(ReentrantFailedWriteRollbackAsync)),
    ("Native brightness ranges normalize, clamp, and round correctly", RangeNormalizationAsync),
    ("Empty ranges and invalid percentages are rejected", RangeValidationAsync),
    ("Refresh preserves metadata and isolates each display read failure", RefreshReadFailureAsync),
    ("Failed discovery retains usable current devices", FailedDiscoveryAsync),
    ("Refresh disposes replaced devices and keeps new devices usable", ReplacedDevicesAsync),
    ("Service rejects unknown devices and invalid percentages", InvalidWriteAsync),
    ("Concurrent writes serialize native operations", SerializedWritesAsync),
    ("Queued canceled writes never reach a device", CanceledQueuedWriteAsync),
    ("Refresh waits for a native write before replacing handles", SerializedRefreshAsync),
    ("Dispose waits for native work and is safe to repeat", SerializedDisposeAsync),
    ("Native reads block writes and canceled queued refresh never discovers", CanceledQueuedRefreshAsync),
    ("Duplicate device IDs release rejected handles and retain current devices", DuplicateDeviceIdsAsync),
    ("Refresh cleans up every old handle despite a disposal failure", RefreshDisposalFailureAsync),
    ("Final disposal attempts all handles and reports failures once", FinalDisposalFailureAsync),
    ("Out-of-range driver results become display errors or rejected writes", InvalidDriverResultsAsync),
    ("An initial unavailable display is omitted from the visible list", () => UiThreadContext.RunAsync(InitialUnavailableSliderAsync)),
    ("Refresh keeps a paused row visible, then omits it if its display is unavailable", () => UiThreadContext.RunAsync(RefreshUnavailableSliderAsync)),
    ("Refresh restores a recovered display and its slider can write again", () => UiThreadContext.RunAsync(RecoveredSliderAsync)),
    ("Slider drag coalesces to the newest value on the UI thread", () => UiThreadContext.RunAsync(SliderCoalescingAsync)),
    ("Immediate refresh drains the latest selected brightness", () => UiThreadContext.RunAsync(ImmediateRefreshAsync)),
    ("Refresh drains the last slider value behind an in-flight write", () => UiThreadContext.RunAsync(RefreshWithPendingWriteAsync)),
    ("Failed refresh retains and resumes existing usable rows", () => UiThreadContext.RunAsync(FailedFlyoutRefreshAsync)),
    ("Exit freezes every row and drains their selected brightness", () => UiThreadContext.RunAsync(FlyoutDisposeAsync)),
    ("A slider notification can refresh without losing its selected value", () => UiThreadContext.RunAsync(ReentrantSliderRefreshAsync)),
    ("A slider notification can dispose while its selected write drains", () => UiThreadContext.RunAsync(ReentrantSliderDisposeAsync)),
    ("Refresh notification listeners join the published refresh operation", () => UiThreadContext.RunAsync(ReentrantRefreshAsync)),
    ("Dispose during a refresh notification waits and prevents new discovery", () => UiThreadContext.RunAsync(ReentrantRefreshDisposeAsync)),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run().WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}\n{exception}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static async Task RejectedWriteRecoveryAsync()
{
    var tracker = new NativeOperationTracker();
    var rejected = new FakeDevice("panel", tracker)
    {
        Read = () => 54,
        Set = percent =>
        {
            WmiBrightnessResult.Validate(true, 5u, (output, _) => output);
            return percent;
        }
    };
    IBrightnessDevice[] devices = [rejected, new FakeDevice("external", tracker)];
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new(devices, [])));
    await using var flyout = new FlyoutViewModel(service);
    await flyout.RefreshAsync();
    var panel = flyout.Displays[0];
    panel.Brightness = 60;
    await panel.PauseAndDrainAsync();
    Equal(54d, panel.Brightness);
    Equal("—", panel.ValueLabel);
    Equal(true, panel.UnavailableReason?.Contains("code 5", StringComparison.Ordinal));
    Equal(1, flyout.Displays.Count);
    Equal("external", flyout.Displays.Single().Id);
    Equal(true, flyout.Displays.Single().IsAvailable);
    panel.Brightness = 70;
    Equal(1, rejected.SetCount);

    var recovered = new FakeDevice("panel", tracker) { Read = () => 54 };
    devices = [recovered, new FakeDevice("external", tracker)];
    await flyout.RefreshAsync();
    Equal(2, flyout.Displays.Count);
    var restored = flyout.Displays[0];
    Equal(true, restored.IsAvailable);
    restored.Brightness = 55;
    await restored.PauseAndDrainAsync();
    Equal(1, recovered.SetCount);
    Equal(55d, restored.Brightness);
}

static async Task LastDisplayWriteFailureAsync()
{
    var tracker = new NativeOperationTracker();
    var device = new FakeDevice("panel", tracker) { Set = _ => throw new IOException("Display disconnected") };
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([device], [])));
    var flyout = new FlyoutViewModel(service);
    await flyout.RefreshAsync();
    var row = flyout.Displays.Single();
    Task? dispose = null;
    flyout.Displays.CollectionChanged += (_, _) =>
    {
        if (flyout.Displays.Count == 0) dispose = flyout.DisposeAsync().AsTask();
    };
    row.Brightness = 60;
    await row.PauseAndDrainAsync();
    await (dispose ?? throw new InvalidOperationException("The failed row was not hidden."));
    Equal(0, flyout.Displays.Count);
    Equal(false, row.IsAvailable);
    Equal(false, flyout.CanRefresh);
    Equal(1, device.SetCount);
}

static async Task FailedRefreshKeepsFailedRowHiddenAsync()
{
    var tracker = new NativeOperationTracker();
    var failed = new FakeDevice("off", tracker) { Set = _ => throw new IOException("Display disconnected") };
    var working = new FakeDevice("on", tracker);
    var failDiscovery = false;
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => failDiscovery
        ? throw new IOException("Discovery failed") : new([failed, working], [])));
    await using var flyout = new FlyoutViewModel(service);
    await flyout.RefreshAsync();
    var row = flyout.Displays.Single(display => display.Id == failed.Id);
    row.Brightness = 60;
    await row.PauseAndDrainAsync();
    failDiscovery = true;
    await flyout.RefreshAsync();
    Equal(1, flyout.Displays.Count);
    Equal(working.Id, flyout.Displays.Single().Id);
    Equal(true, flyout.Displays.Single().IsAvailable);
    Equal(false, row.IsControllable);
    Equal(false, row.IsAvailable);
    Equal(true, flyout.Status.Contains("Discovery failed", StringComparison.Ordinal));
    row.Brightness = 70;
    Equal(1, failed.SetCount);
}

static async Task ReentrantFailedWriteRollbackAsync()
{
    var tracker = new NativeOperationTracker();
    var device = new FakeDevice("panel", tracker) { Set = _ => throw new IOException("Display disconnected") };
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([device], [])));
    await using var flyout = new FlyoutViewModel(service);
    await flyout.RefreshAsync();
    var row = flyout.Displays.Single();
    var hidden = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var rollbackNotifications = 0;
    var editableDuringRollback = false;
    row.PropertyChanged += (_, change) =>
    {
        if (change.PropertyName != nameof(DisplayViewModel.Brightness) || row.Brightness != 50) return;
        rollbackNotifications++;
        editableDuringRollback |= row.IsAvailable;
        row.Brightness = 80;
    };
    flyout.Displays.CollectionChanged += (_, _) =>
    {
        if (flyout.Displays.Count == 0) hidden.TrySetResult();
    };
    row.Brightness = 60;
    // Do not pause before the failure: that would independently prevent the reentrant edit.
    await hidden.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await row.PauseAndDrainAsync();
    Equal(1, rollbackNotifications);
    Equal(false, editableDuringRollback);
    Equal(50d, row.Brightness);
    Equal(1, device.SetCount);
    Equal(0, flyout.Displays.Count);
    Equal(true, flyout.CanRefresh);
    Equal(true, flyout.Status.StartsWith("No controllable displays", StringComparison.Ordinal));
}

static Task RangeNormalizationAsync()
{
    var range = new BrightnessRange(10, 110);
    Equal(0, range.ToPercent(0));
    Equal(0, range.ToPercent(10));
    Equal(50, range.ToPercent(60));
    Equal(100, range.ToPercent(110));
    Equal(100, range.ToPercent(uint.MaxValue));
    for (var percent = 0; percent <= 100; percent++)
    {
        Equal((uint)(10 + percent), range.FromPercent(percent));
        Equal(percent, range.ToPercent(range.FromPercent(percent)));
    }

    var coarseRange = new BrightnessRange(0, 200);
    Equal(1, coarseRange.ToPercent(1)); // 0.5% rounds away from zero.
    Equal(1u, new BrightnessRange(0, 1).FromPercent(50));
    var largeRange = new BrightnessRange(0, uint.MaxValue);
    Equal(uint.MaxValue, largeRange.FromPercent(100));
    Equal(50, largeRange.ToPercent(largeRange.FromPercent(50)));
    var highMinimum = new BrightnessRange(uint.MaxValue - 100, uint.MaxValue);
    Equal(uint.MaxValue - 50, highMinimum.FromPercent(50));
    Equal(50, highMinimum.ToPercent(uint.MaxValue - 50));
    return Task.CompletedTask;
}

static Task RangeValidationAsync()
{
    Throws<ArgumentOutOfRangeException>(() => new BrightnessRange(0, 0));
    Throws<ArgumentOutOfRangeException>(() => new BrightnessRange(10, 10));
    Throws<ArgumentOutOfRangeException>(() => new BrightnessRange(10, 9));
    var range = new BrightnessRange(0, 100);
    Throws<ArgumentOutOfRangeException>(() => range.FromPercent(-1));
    Throws<ArgumentOutOfRangeException>(() => range.FromPercent(101));
    return Task.CompletedTask;
}

static async Task RefreshReadFailureAsync()
{
    var tracker = new NativeOperationTracker();
    var failing = new FakeDevice("bad", tracker) { Read = () => throw new IOException("Read failed") };
    var good = new FakeDevice("good", tracker) { Read = () => 42 };
    var issue = new DisplayIssue("Unsupported", "Brightness control is unavailable");
    var discovery = new FakeDiscovery(tracker, () => new([failing, good], [issue]));
    await using var service = new BrightnessService(discovery);

    var result = await service.RefreshAsync();

    Equal(2, result.Displays.Count);
    Equal("bad", result.Displays[0].Id);
    Equal(failing.Name, result.Displays[0].Name);
    Equal(failing.ConnectionLabel, result.Displays[0].ConnectionLabel);
    Equal<int?>(null, result.Displays[0].BrightnessPercent);
    Equal("Read failed", result.Displays[0].Error);
    Equal<int?>(42, result.Displays[1].BrightnessPercent);
    Equal<string?>(null, result.Displays[1].Error);
    Equal(issue, result.Issues.Single());
    Equal(1, failing.ReadCount);
    Equal(1, good.ReadCount);
    Equal(1, tracker.MaximumConcurrentOperations);
    // A read failure need not imply the hardware write will also fail.
    Equal(30, await service.SetBrightnessAsync("bad", 30));
}

static async Task FailedDiscoveryAsync()
{
    var tracker = new NativeOperationTracker();
    var device = new FakeDevice("existing", tracker);
    var fail = false;
    var discovery = new FakeDiscovery(tracker, () => fail
        ? throw new IOException("Discovery failed")
        : new([device], []));
    await using var service = new BrightnessService(discovery);
    await service.RefreshAsync();
    fail = true;

    await ThrowsAsync<IOException>(() => service.RefreshAsync());

    Equal(0, device.DisposeCount);
    Equal(70, await service.SetBrightnessAsync(device.Id, 70));
    Equal(1, device.SetCount);
}

static async Task ReplacedDevicesAsync()
{
    var tracker = new NativeOperationTracker();
    var previous = new FakeDevice("previous", tracker);
    var current = new FakeDevice("current", tracker);
    var devices = new[] { previous };
    var discovery = new FakeDiscovery(tracker, () => new(devices, []));
    await using var service = new BrightnessService(discovery);
    await service.RefreshAsync();
    devices = [current];

    var result = await service.RefreshAsync();

    Equal(1, previous.DisposeCount);
    Equal(0, current.DisposeCount);
    Equal("current", result.Displays.Single().Id);
    await ThrowsAsync<InvalidOperationException>(() => service.SetBrightnessAsync(previous.Id, 25));
    Equal(25, await service.SetBrightnessAsync(current.Id, 25));
    await service.DisposeAsync();
    await service.DisposeAsync();
    Equal(1, previous.DisposeCount);
    Equal(1, current.DisposeCount);
}

static async Task InvalidWriteAsync()
{
    var tracker = new NativeOperationTracker();
    var device = new FakeDevice("display", tracker);
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([device], [])));
    await service.RefreshAsync();

    await ThrowsAsync<InvalidOperationException>(() => service.SetBrightnessAsync("missing", 20));
    await ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetBrightnessAsync(device.Id, -1));
    await ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetBrightnessAsync(device.Id, 101));
    Equal(0, device.SetCount);
    device.Set = percent => percent - 1;
    Equal(49, await service.SetBrightnessAsync(device.Id, 50));
}

static async Task SerializedWritesAsync()
{
    var tracker = new NativeOperationTracker();
    using var blocked = new BlockingNativeOperation();
    var device = new FakeDevice("display", tracker) { Set = percent =>
    {
        if (percent == 20) blocked.Execute();
        return percent;
    } };
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([device], [])));
    await service.RefreshAsync();
    var first = service.SetBrightnessAsync(device.Id, 20);
    await blocked.Entered.WaitAsync(TimeSpan.FromSeconds(5));
    var second = service.SetBrightnessAsync(device.Id, 80);
    try
    {
        Equal(false, second.IsCompleted);
        Equal(1, device.SetCount);
    }
    finally { blocked.Release(); }

    Equal(20, await first);
    Equal(80, await second);
    Equal(2, device.SetCount);
    Equal(1, tracker.MaximumConcurrentOperations);
}

static async Task CanceledQueuedWriteAsync()
{
    var tracker = new NativeOperationTracker();
    using var blocked = new BlockingNativeOperation();
    var device = new FakeDevice("display", tracker) { Set = percent =>
    {
        if (percent == 20) blocked.Execute();
        return percent;
    } };
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([device], [])));
    await service.RefreshAsync();
    var first = service.SetBrightnessAsync(device.Id, 20);
    await blocked.Entered.WaitAsync(TimeSpan.FromSeconds(5));
    using var cancellation = new CancellationTokenSource();
    var canceled = service.SetBrightnessAsync(device.Id, 80, cancellation.Token);
    cancellation.Cancel();
    try
    {
        await ThrowsAsync<OperationCanceledException>(() => canceled);
        Equal(1, device.SetCount);
    }
    finally { blocked.Release(); }

    await first;
    Equal(1, device.SetCount);
    Equal(50, await service.SetBrightnessAsync(device.Id, 50));
}

static async Task SerializedRefreshAsync()
{
    var tracker = new NativeOperationTracker();
    using var blocked = new BlockingNativeOperation();
    var previous = new FakeDevice("previous", tracker) { Set = percent =>
    {
        blocked.Execute();
        return percent;
    } };
    var current = new FakeDevice("current", tracker);
    var devices = new[] { previous };
    var discovery = new FakeDiscovery(tracker, () => new(devices, []));
    await using var service = new BrightnessService(discovery);
    await service.RefreshAsync();
    var write = service.SetBrightnessAsync(previous.Id, 20);
    await blocked.Entered.WaitAsync(TimeSpan.FromSeconds(5));
    devices = [current];
    var refresh = service.RefreshAsync();
    try
    {
        Equal(false, refresh.IsCompleted);
        Equal(1, discovery.DiscoverCount);
        Equal(0, previous.DisposeCount);
        Equal(0, current.ReadCount);
    }
    finally { blocked.Release(); }

    await write;
    Equal("current", (await refresh).Displays.Single().Id);
    Equal(1, previous.DisposeCount);
    Equal(1, current.ReadCount);
    Equal(1, tracker.MaximumConcurrentOperations);
}

static async Task SerializedDisposeAsync()
{
    var tracker = new NativeOperationTracker();
    using var blocked = new BlockingNativeOperation();
    var device = new FakeDevice("display", tracker) { Set = percent =>
    {
        blocked.Execute();
        return percent;
    } };
    var service = new BrightnessService(new FakeDiscovery(tracker, () => new([device], [])));
    try
    {
        await service.RefreshAsync();
        var write = service.SetBrightnessAsync(device.Id, 20);
        await blocked.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var dispose = service.DisposeAsync().AsTask();
        try
        {
            Equal(false, dispose.IsCompleted);
            Equal(0, device.DisposeCount);
        }
        finally { blocked.Release(); }

        await write;
        await dispose;
        await service.DisposeAsync();
        Equal(1, device.DisposeCount);
        Equal(1, tracker.MaximumConcurrentOperations);
        await ThrowsAsync<ObjectDisposedException>(() => service.RefreshAsync());
        await ThrowsAsync<ObjectDisposedException>(() => service.SetBrightnessAsync(device.Id, 20));
    }
    finally
    {
        blocked.Release();
        await service.DisposeAsync();
    }
}

static async Task CanceledQueuedRefreshAsync()
{
    var tracker = new NativeOperationTracker();
    using var blocked = new BlockingNativeOperation();
    var device = new FakeDevice("display", tracker) { Read = () =>
    {
        blocked.Execute();
        return 45;
    } };
    var discovery = new FakeDiscovery(tracker, () => new([device], []));
    await using var service = new BrightnessService(discovery);
    var firstRefresh = service.RefreshAsync();
    await blocked.Entered.WaitAsync(TimeSpan.FromSeconds(5));
    var queuedWrite = service.SetBrightnessAsync(device.Id, 60);
    using var cancellation = new CancellationTokenSource();
    var canceledRefresh = service.RefreshAsync(cancellation.Token);
    cancellation.Cancel();
    try
    {
        await ThrowsAsync<OperationCanceledException>(() => canceledRefresh);
        Equal(1, discovery.DiscoverCount);
        Equal(0, device.DisposeCount);
        Equal(false, queuedWrite.IsCompleted);
        Equal(0, device.SetCount);
    }
    finally { blocked.Release(); }

    Equal<int?>(45, (await firstRefresh).Displays.Single().BrightnessPercent);
    Equal(60, await queuedWrite);
    Equal(1, tracker.MaximumConcurrentOperations);
}

static async Task DuplicateDeviceIdsAsync()
{
    var tracker = new NativeOperationTracker();
    var existing = new FakeDevice("existing", tracker);
    var firstDuplicate = new FakeDevice("duplicate", tracker);
    var secondDuplicate = new FakeDevice("duplicate", tracker);
    var devices = new[] { existing };
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new(devices, [])));
    await service.RefreshAsync();
    devices = [firstDuplicate, secondDuplicate];

    await ThrowsAsync<InvalidOperationException>(() => service.RefreshAsync());

    Equal(1, firstDuplicate.DisposeCount);
    Equal(1, secondDuplicate.DisposeCount);
    Equal(0, existing.DisposeCount);
    Equal(0, firstDuplicate.ReadCount);
    Equal(0, secondDuplicate.ReadCount);
    Equal(35, await service.SetBrightnessAsync(existing.Id, 35));
}

static async Task RefreshDisposalFailureAsync()
{
    var tracker = new NativeOperationTracker();
    var failing = new FakeDevice("failing", tracker) { OnDispose = () => throw new IOException("Dispose failed") };
    var other = new FakeDevice("other", tracker);
    var current = new FakeDevice("current", tracker);
    var devices = new[] { failing, other };
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new(devices, [])));
    await service.RefreshAsync();
    devices = [current];

    var result = await service.RefreshAsync();

    Equal(1, failing.DisposeCount);
    Equal(1, other.DisposeCount);
    Equal(0, current.DisposeCount);
    Equal(current.Id, result.Displays.Single().Id);
    Equal(true, result.Issues.Any(issue => issue.Message.Contains("Dispose failed", StringComparison.Ordinal)));
    Equal(35, await service.SetBrightnessAsync(current.Id, 35));
}

static async Task FinalDisposalFailureAsync()
{
    var tracker = new NativeOperationTracker();
    var failing = new FakeDevice("failing", tracker) { OnDispose = () => throw new IOException("Dispose failed") };
    var other = new FakeDevice("other", tracker);
    var service = new BrightnessService(new FakeDiscovery(tracker, () => new([failing, other], [])));
    await service.RefreshAsync();

    await ThrowsAsync<AggregateException>(() => service.DisposeAsync().AsTask());

    Equal(1, failing.DisposeCount);
    Equal(1, other.DisposeCount);
    await service.DisposeAsync();
    Equal(1, failing.DisposeCount);
    Equal(1, other.DisposeCount);
    await ThrowsAsync<ObjectDisposedException>(() => service.RefreshAsync());
}

static async Task InvalidDriverResultsAsync()
{
    var tracker = new NativeOperationTracker();
    var belowRange = new FakeDevice("below", tracker) { Read = () => -1 };
    var aboveRange = new FakeDevice("above", tracker) { Read = () => 101 };
    var good = new FakeDevice("good", tracker);
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([belowRange, aboveRange, good], [])));

    var result = await service.RefreshAsync();

    foreach (var invalid in result.Displays.Take(2))
    {
        Equal<int?>(null, invalid.BrightnessPercent);
        Equal(false, string.IsNullOrWhiteSpace(invalid.Error));
    }
    Equal<int?>(50, result.Displays[2].BrightnessPercent);
    good.Set = _ => -1;
    await ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetBrightnessAsync(good.Id, 50));
    good.Set = _ => 101;
    await ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetBrightnessAsync(good.Id, 50));
    good.Set = _ => 100;
    Equal(100, await service.SetBrightnessAsync(good.Id, 100));
}

static async Task InitialUnavailableSliderAsync()
{
    var tracker = new NativeOperationTracker();
    var unavailable = new FakeDevice("off", tracker) { Read = () => throw new IOException("Display is off") };
    var available = new FakeDevice("on", tracker) { Read = () => 42 };
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([unavailable, available], [])));
    await using var flyout = new FlyoutViewModel(service);

    await flyout.RefreshAsync();

    Equal(1, flyout.Displays.Count);
    Equal(false, flyout.Displays.Any(display => display.Id == unavailable.Id));
    Equal(0, unavailable.SetCount);
    Equal(true, flyout.Displays.Single(display => display.Id == available.Id).IsAvailable);
    Equal(0, available.SetCount);
}

static async Task RefreshUnavailableSliderAsync()
{
    var tracker = new NativeOperationTracker();
    using var blockedRead = new BlockingNativeOperation();
    var previous = new FakeDevice("display", tracker) { Read = () => 55 };
    var unavailable = new FakeDevice("display", tracker) { Read = () =>
    {
        blockedRead.Execute();
        throw new IOException("Display is off");
    } };
    var devices = new[] { previous };
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new(devices, [])));
    await using var flyout = new FlyoutViewModel(service);
    await flyout.RefreshAsync();
    var oldRow = flyout.Displays.Single();
    Equal(true, oldRow.IsAvailable);
    devices = [unavailable];

    var refresh = flyout.RefreshAsync();
    try
    {
        // Cached brightness must not keep the old slider interactive during the native read.
        Equal(false, oldRow.IsAvailable);
        Equal(true, oldRow.IsControllable);
        Equal(true, ReferenceEquals(oldRow, flyout.Displays.Single()));
        oldRow.Brightness = 80;
        await blockedRead.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        Equal(false, refresh.IsCompleted);
        Equal(0, previous.SetCount);
    }
    finally { blockedRead.Release(); }
    await refresh;

    Equal(0, flyout.Displays.Count);
    Equal(1, previous.DisposeCount);
    oldRow.Resume();
    Equal(false, oldRow.IsAvailable);
    Equal(55d, oldRow.Brightness);
    Equal(true, flyout.Status.StartsWith("No controllable displays", StringComparison.Ordinal));
    Equal(true, flyout.CanRefresh);
    Equal(0, previous.SetCount);
    Equal(0, unavailable.SetCount);
    Equal(1, tracker.MaximumConcurrentOperations);
}

static async Task RecoveredSliderAsync()
{
    var tracker = new NativeOperationTracker();
    var unavailable = new FakeDevice("display", tracker) { Read = () => throw new IOException("Display is off") };
    var brightness = 55;
    var recovered = new FakeDevice("display", tracker) { Read = () => brightness, Set = percent => brightness = percent };
    var devices = new[] { unavailable };
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new(devices, [])));
    await using var flyout = new FlyoutViewModel(service);
    await flyout.RefreshAsync();
    Equal(0, flyout.Displays.Count);
    devices = [recovered];

    await flyout.RefreshAsync();

    var row = flyout.Displays.Single();
    Equal(1, unavailable.DisposeCount);
    Equal(true, row.IsAvailable);
    Equal("55%", row.ValueLabel);
    Equal<string?>(null, row.UnavailableReason);
    row.Brightness = 70;
    await row.PauseAndDrainAsync();
    row.Resume();
    Equal(true, row.IsAvailable);
    Equal(70, brightness);
    Equal("70%", row.ValueLabel);
    Equal(0, unavailable.SetCount);
    Equal(1, recovered.SetCount);
}

static async Task SliderCoalescingAsync()
{
    var tracker = new NativeOperationTracker();
    var requested = new List<int>();
    var device = new FakeDevice("display", tracker) { Set = percent =>
    {
        requested.Add(percent);
        return percent;
    } };
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([device], [])));
    var display = (await service.RefreshAsync()).Displays.Single();
    await using var row = new DisplayViewModel(display, service);
    var uiThread = Environment.CurrentManagedThreadId;
    var notificationThreads = new List<int>();
    row.PropertyChanged += (_, _) => notificationThreads.Add(Environment.CurrentManagedThreadId);

    row.Brightness = 10;
    row.Brightness = 30;
    row.Brightness = 90;
    var drain = row.PauseAndDrainAsync();
    row.Brightness = 5; // Refresh has frozen this slider.
    await drain;

    Equal(1, requested.Count);
    Equal(90, requested.Single());
    Equal(90d, row.Brightness);
    Equal("90%", row.ValueLabel);
    Equal(false, row.IsAvailable);
    Equal(true, notificationThreads.Count > 0 && notificationThreads.All(id => id == uiThread));
    row.Resume();
    Equal(true, row.IsAvailable);
}

static async Task ImmediateRefreshAsync()
{
    var tracker = new NativeOperationTracker();
    var brightness = 50;
    var previous = new FakeDevice("display", tracker) { Read = () => brightness, Set = percent => brightness = percent };
    var replacement = new FakeDevice("display", tracker) { Read = () => brightness };
    var devices = new[] { previous };
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new(devices, [])));
    await using var flyout = new FlyoutViewModel(service);
    await flyout.RefreshAsync();
    var oldRow = flyout.Displays.Single();
    oldRow.Brightness = 80;
    devices = [replacement];

    var refresh = flyout.RefreshAsync();
    Equal(false, oldRow.IsAvailable);
    oldRow.Brightness = 5;
    await refresh;

    Equal(80, brightness);
    Equal(1, previous.SetCount);
    Equal(1, previous.DisposeCount);
    Equal(false, oldRow.IsAvailable);
    Equal(false, ReferenceEquals(oldRow, flyout.Displays.Single()));
    Equal(80d, flyout.Displays.Single().Brightness);
    Equal(true, flyout.Displays.Single().IsAvailable);
}

static async Task RefreshWithPendingWriteAsync()
{
    var tracker = new NativeOperationTracker();
    using var blocked = new BlockingNativeOperation();
    var brightness = 50;
    var requested = new List<int>();
    var previous = new FakeDevice("display", tracker) { Read = () => brightness, Set = percent =>
    {
        requested.Add(percent);
        if (percent == 20) blocked.Execute();
        return brightness = percent;
    } };
    var replacement = new FakeDevice("display", tracker) { Read = () => brightness };
    var devices = new[] { previous };
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new(devices, [])));
    await using var flyout = new FlyoutViewModel(service);
    await flyout.RefreshAsync();
    var oldRow = flyout.Displays.Single();
    oldRow.Brightness = 20;
    await blocked.Entered.WaitAsync(TimeSpan.FromSeconds(5));
    oldRow.Brightness = 80;
    devices = [replacement];
    var refresh = flyout.RefreshAsync();
    try
    {
        Equal(false, oldRow.IsAvailable);
        Equal(false, refresh.IsCompleted);
        Equal(0, previous.DisposeCount);
    }
    finally { blocked.Release(); }

    await refresh;
    Equal(2, requested.Count);
    Equal(20, requested[0]);
    Equal(80, requested[1]);
    Equal(80d, flyout.Displays.Single().Brightness);
    Equal(1, tracker.MaximumConcurrentOperations);
}

static async Task FailedFlyoutRefreshAsync()
{
    var tracker = new NativeOperationTracker();
    var brightness = 50;
    var failDiscovery = false;
    var device = new FakeDevice("display", tracker) { Read = () => brightness, Set = percent => brightness = percent };
    var issue = new DisplayIssue("Other screen", "Unsupported");
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => failDiscovery
        ? throw new IOException("Discovery failed")
        : new([device], [issue])));
    await using var flyout = new FlyoutViewModel(service);
    await flyout.RefreshAsync();
    var row = flyout.Displays.Single();
    row.Brightness = 40;
    failDiscovery = true;

    await flyout.RefreshAsync();

    Equal(40, brightness);
    Equal(true, ReferenceEquals(row, flyout.Displays.Single()));
    Equal(true, row.IsAvailable);
    Equal(0, device.DisposeCount);
    Equal(issue, flyout.Issues.Single());
    Equal(true, flyout.Status.Contains("Discovery failed", StringComparison.Ordinal));
    Equal(true, flyout.CanRefresh);
    row.Brightness = 65;
    await row.PauseAndDrainAsync();
    Equal(65, brightness);
}

static async Task FlyoutDisposeAsync()
{
    var tracker = new NativeOperationTracker();
    var first = new FakeDevice("first", tracker);
    var second = new FakeDevice("second", tracker);
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([first, second], [])));
    var flyout = new FlyoutViewModel(service);
    await flyout.RefreshAsync();
    var firstRow = flyout.Displays[0];
    var secondRow = flyout.Displays[1];
    firstRow.Brightness = 25;
    secondRow.Brightness = 75;

    var dispose = flyout.DisposeAsync().AsTask();

    Equal(false, firstRow.IsAvailable);
    Equal(false, secondRow.IsAvailable);
    firstRow.Brightness = 5;
    secondRow.Brightness = 5;
    await dispose;
    await flyout.DisposeAsync();
    Equal(1, first.SetCount);
    Equal(1, second.SetCount);
    Equal(25d, firstRow.Brightness);
    Equal(75d, secondRow.Brightness);
    Equal(false, flyout.CanRefresh);
    Equal(0, first.DisposeCount);
    Equal(0, second.DisposeCount);
    Equal(1, tracker.MaximumConcurrentOperations);
}

static async Task ReentrantSliderRefreshAsync()
{
    var tracker = new NativeOperationTracker();
    var brightness = 50;
    var previous = new FakeDevice("display", tracker) { Read = () => brightness, Set = percent => brightness = percent };
    var replacement = new FakeDevice("display", tracker) { Read = () => brightness };
    var devices = new[] { previous };
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new(devices, [])));
    await using var flyout = new FlyoutViewModel(service);
    await flyout.RefreshAsync();
    var row = flyout.Displays.Single();
    Task? reentrantRefresh = null;
    row.PropertyChanged += (_, change) =>
    {
        if (change.PropertyName == nameof(DisplayViewModel.Brightness) && reentrantRefresh is null)
            reentrantRefresh = flyout.RefreshAsync();
    };
    devices = [replacement];

    row.Brightness = 80;
    await (reentrantRefresh ?? throw new InvalidOperationException("Refresh listener did not run."));

    Equal(1, previous.SetCount);
    Equal(1, previous.DisposeCount);
    Equal(80, brightness);
    Equal(80d, flyout.Displays.Single().Brightness);
}

static async Task ReentrantSliderDisposeAsync()
{
    var tracker = new NativeOperationTracker();
    var device = new FakeDevice("display", tracker);
    await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([device], [])));
    var display = (await service.RefreshAsync()).Displays.Single();
    await using var row = new DisplayViewModel(display, service);
    Task? reentrantDispose = null;
    row.PropertyChanged += (_, change) =>
    {
        if (change.PropertyName == nameof(DisplayViewModel.Brightness) && reentrantDispose is null)
            reentrantDispose = row.DisposeAsync().AsTask();
    };

    row.Brightness = 80;
    await (reentrantDispose ?? throw new InvalidOperationException("Dispose listener did not run."));

    Equal(1, device.SetCount);
    Equal(80d, row.Brightness);
    Equal(false, row.IsAvailable);
}

static async Task ReentrantRefreshAsync()
{
    var tracker = new NativeOperationTracker();
    var discovery = new FakeDiscovery(tracker, () => new([new FakeDevice("display", tracker)], []));
    await using var service = new BrightnessService(discovery);
    await using var flyout = new FlyoutViewModel(service);
    Task? nestedRefresh = null;
    var listenerRan = false;
    flyout.PropertyChanged += (_, change) =>
    {
        if (change.PropertyName != nameof(FlyoutViewModel.IsRefreshing) || !flyout.IsRefreshing || listenerRan) return;
        listenerRan = true;
        nestedRefresh = flyout.RefreshAsync();
    };

    var refresh = flyout.RefreshAsync();

    Equal(true, ReferenceEquals(refresh, nestedRefresh));
    await refresh;
    Equal(1, discovery.DiscoverCount);
    Equal(1, flyout.Displays.Count);
    Equal(false, flyout.IsRefreshing);
}

static async Task ReentrantRefreshDisposeAsync()
{
    var tracker = new NativeOperationTracker();
    var discovery = new FakeDiscovery(tracker, () => new([new FakeDevice("display", tracker)], []));
    await using var service = new BrightnessService(discovery);
    var flyout = new FlyoutViewModel(service);
    Task? dispose = null;
    var disposeWasPendingInListener = false;
    var listenerRan = false;
    flyout.PropertyChanged += (_, change) =>
    {
        if (change.PropertyName != nameof(FlyoutViewModel.IsRefreshing) || !flyout.IsRefreshing || listenerRan) return;
        listenerRan = true;
        dispose = flyout.DisposeAsync().AsTask();
        disposeWasPendingInListener = !dispose.IsCompleted;
    };

    await flyout.RefreshAsync();
    await (dispose ?? throw new InvalidOperationException("Dispose listener did not run."));

    Equal(true, disposeWasPendingInListener);
    Equal(0, discovery.DiscoverCount);
    Equal(0, flyout.Displays.Count);
    Equal(false, flyout.CanRefresh);
    var repeatNotifications = 0;
    flyout.PropertyChanged += (_, change) =>
    {
        if (change.PropertyName == nameof(FlyoutViewModel.CanRefresh)) repeatNotifications++;
    };
    await flyout.DisposeAsync();
    await flyout.DisposeAsync();
    Equal(0, repeatNotifications);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected ?? (object)"<null>"}, got {actual ?? (object)"<null>"}.");
}

static void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed class FakeDiscovery(NativeOperationTracker tracker, Func<DisplayDiscoveryResult> discover) : IDisplayDiscovery
{
    private int _discoverCount;
    public int DiscoverCount => Volatile.Read(ref _discoverCount);

    public DisplayDiscoveryResult Discover()
    {
        using var operation = tracker.Enter();
        Interlocked.Increment(ref _discoverCount);
        return discover();
    }
}

sealed class FakeDevice(string id, NativeOperationTracker tracker) : IBrightnessDevice
{
    private int _readCount;
    private int _setCount;
    private int _disposeCount;
    public string Id => id;
    public string Name => $"Display {id}";
    public string ConnectionLabel => "Fake connection";
    public Func<int> Read { get; init; } = () => 50;
    public Func<int, int> Set { get; set; } = percent => percent;
    public Action OnDispose { get; init; } = () => { };
    public int ReadCount => Volatile.Read(ref _readCount);
    public int SetCount => Volatile.Read(ref _setCount);
    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public int ReadBrightness()
    {
        using var operation = tracker.Enter();
        Interlocked.Increment(ref _readCount);
        ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
        return Read();
    }

    public int SetBrightness(int percent)
    {
        using var operation = tracker.Enter();
        Interlocked.Increment(ref _setCount);
        ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
        return Set(percent);
    }

    public void Dispose()
    {
        using var operation = tracker.Enter();
        Interlocked.Increment(ref _disposeCount);
        OnDispose();
    }
}

sealed class NativeOperationTracker
{
    private int _active;
    private int _maximum;
    public int MaximumConcurrentOperations => Volatile.Read(ref _maximum);

    public IDisposable Enter()
    {
        var active = Interlocked.Increment(ref _active);
        int observed;
        do
        {
            observed = Volatile.Read(ref _maximum);
            if (active <= observed) break;
        } while (Interlocked.CompareExchange(ref _maximum, active, observed) != observed);
        return new Operation(this);
    }

    private sealed class Operation(NativeOperationTracker owner) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref owner._active);
    }
}

sealed class BlockingNativeOperation : IDisposable
{
    private readonly ManualResetEventSlim _release = new(false);
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Entered => _entered.Task;

    public void Execute()
    {
        _entered.TrySetResult();
        if (!_release.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The test did not release its blocked native operation.");
    }

    public void Release() => _release.Set();
    public void Dispose() => _release.Dispose();
}

sealed class UiThreadContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<Action> _callbacks = new();

    public override void Post(SendOrPostCallback callback, object? state) =>
        _callbacks.Add(() => callback(state));

    public static Task RunAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var context = new UiThreadContext();
            SetSynchronizationContext(context);
            context.Post(async _ =>
            {
                try
                {
                    await action();
                    completion.TrySetResult();
                }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { context._callbacks.CompleteAdding(); }
            }, null);
            foreach (var callback in context._callbacks.GetConsumingEnumerable()) callback();
        }) { IsBackground = true, Name = "ViewModel regression UI thread" };
        thread.Start();
        return completion.Task;
    }

    public void Dispose() => _callbacks.Dispose();
}
