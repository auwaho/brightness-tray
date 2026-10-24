using BrightnessTray.App.ViewModels;
using BrightnessTray.Core;

internal static class SliderWheelTests
{
    internal static Task InputAndLimitsAsync() => UiThreadContext.RunAsync(async () =>
    {
        var tracker = new NativeOperationTracker();
        var value = 40;
        var first = new FakeDevice("first", tracker) { Read = () => value, Set = percent => value = percent };
        var second = new FakeDevice("second", tracker) { Read = () => 80 };
        await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([first, second], [])));
        await using var flyout = new FlyoutViewModel(service);
        await flyout.RefreshAsync();
        var row = flyout.Displays[0];
        Require(row.AdjustBrightnessFromWheel(60) && row.Brightness == 40, "Partial wheel input changed brightness too early.");
        Require(row.AdjustBrightnessFromWheel(60) && row.Brightness == 45, "Two partial deltas did not form a five-point step.");
        row.AdjustBrightnessFromWheel(240);
        row.AdjustBrightnessFromWheel(-120);
        await row.PauseAndDrainAsync();
        Require(value == 50 && first.SetCount == 1 && second.SetCount == 0 && flyout.Displays[1].Brightness == 80,
            "Slider wheel requests were not coalesced or affected another display.");
        Require(!row.AdjustBrightnessFromWheel(120) && row.Brightness == 50, "A paused row accepted wheel input.");
        row.Resume();
        row.Brightness = 20;
        row.AdjustBrightnessFromWheel(120);
        await row.PauseAndDrainAsync();
        Require(value == 25, "A manual edit did not reset the wheel's baseline.");
        row.Resume();
        row.AdjustBrightnessFromWheel(-120000);
        Require(row.Brightness == 0, "Slider wheel input escaped the minimum.");
        row.AdjustBrightnessFromWheel(120000);
        row.AdjustBrightnessFromWheel(-120);
        await row.PauseAndDrainAsync();
        Require(value == 95, "Slider wheel input escaped the maximum or retained overshoot.");
    });

    internal static Task CoarseAndFailureAsync() => UiThreadContext.RunAsync(async () =>
    {
        var tracker = new NativeOperationTracker();
        var value = 40;
        var lost = false;
        var device = new FakeDevice("panel", tracker)
        {
            Read = () => value,
            Set = percent => lost ? throw new IOException("Disconnected") : value = percent / 10 * 10,
        };
        await using var service = new BrightnessService(new FakeDiscovery(tracker, () => new([device], [])));
        await using var flyout = new FlyoutViewModel(service);
        await flyout.RefreshAsync();
        var row = flyout.Displays[0];
        row.AdjustBrightnessFromWheel(120);
        await row.PauseAndDrainAsync();
        Require(value == 40, "The coarse test driver did not round its first request.");
        row.Resume();
        row.AdjustBrightnessFromWheel(120);
        await row.PauseAndDrainAsync();
        Require(value == 50, "Wheel progress got stuck on the driver's coarse brightness scale.");
        row.Resume();
        lost = true;
        row.AdjustBrightnessFromWheel(120);
        await row.PauseAndDrainAsync();
        var writes = device.SetCount;
        Require(flyout.Displays.Count == 0 && !row.AdjustBrightnessFromWheel(120) && device.SetCount == writes,
            "A failed display accepted another wheel request or remained visible.");
    });

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
