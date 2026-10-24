using System.ComponentModel;
using BrightnessTray.Windows;

internal static class DdcBrightnessTests
{
    internal static Task TransientFallbackRangeAsync()
    {
        foreach (var transientFailures in new[] { 1, 2 })
        {
            var state = new DdcNativeState { HighMinimum = 20, HighMaximum = 120, HighCurrent = 70, VcpMaximum = 200 };
            foreach (var _ in Enumerable.Range(0, transientFailures)) state.VcpReadErrors.Enqueue(121);
            using var device = new DdcBrightnessDevice("ddc", "Simulated display", new SafePhysicalMonitorHandle(state));
            var pauses = new List<TimeSpan>();
            using var retry = new RetryingBrightnessDevice(device, delay: pauses.Add);
            Equal(50, retry.ReadBrightness());
            Equal(60, retry.SetBrightness(60));
            Equal(transientFailures + 1, state.VcpReads);
            Equal(transientFailures + 1, state.HighWrites.Count);
            Equal(true, state.HighWrites.All(value => value == 80));
            Equal("120", string.Join(',', state.VcpWrites));
            Equal(transientFailures, pauses.Count);
            // Once committed, raw VCP range/mode survive subsequent writes without high-level calls.
            Equal(75, retry.SetBrightness(75));
            Equal("120,150", string.Join(',', state.VcpWrites));
            Equal(transientFailures + 1, state.HighWrites.Count);
        }
        return Task.CompletedTask;
    }

    internal static Task FailedFallbackNeverWritesAsync()
    {
        // A permanent failure is not retried; exhausting transient retries must likewise write nothing.
        foreach (var error in new[] { 50, 121 })
        {
            var state = new DdcNativeState { VcpMaximum = 200 };
            for (var attempt = 0; attempt < 3; attempt++) state.VcpReadErrors.Enqueue(error);
            using var device = new DdcBrightnessDevice("ddc", "Simulated display", new SafePhysicalMonitorHandle(state));
            var pauses = 0;
            using var retry = new RetryingBrightnessDevice(device, delay: _ => pauses++);
            Equal(50, retry.ReadBrightness());
            try { retry.SetBrightness(60); throw new InvalidOperationException("The failed probe unexpectedly wrote brightness."); }
            catch (Win32Exception exception) { Equal(error, exception.NativeErrorCode); }
            var attempts = error == 121 ? 3 : 1;
            Equal(attempts, state.VcpReads);
            Equal(attempts, state.HighWrites.Count);
            Equal(attempts - 1, pauses);
            Equal(0, state.VcpWrites.Count);

            // A later successful probe must still establish the raw range after a prior final failure.
            state.VcpReadErrors.Clear();
            Equal(60, retry.SetBrightness(60));
            Equal(attempts + 1, state.VcpReads);
            Equal("120", string.Join(',', state.VcpWrites));
        }
        return Task.CompletedTask;
    }

    internal static Task ReadFallbackAndInvalidRangeAsync()
    {
        var state = new DdcNativeState();
        using var device = new DdcBrightnessDevice("ddc", "Simulated display", new SafePhysicalMonitorHandle(state));
        Equal(50, device.ReadBrightness());
        state.HighReadErrors.Enqueue(50);
        state.VcpReadErrors.Enqueue(121);
        try { device.ReadBrightness(); throw new InvalidOperationException("The failed fallback read unexpectedly succeeded."); }
        catch (Win32Exception exception) { Equal(121, exception.NativeErrorCode); }
        // Failure must not switch the device permanently to VCP: a recovered high-level write is valid.
        state.HighWriteError = 0;
        Equal(60, device.SetBrightness(60));
        Equal("60", string.Join(',', state.HighWrites));
        Equal(0, state.VcpWrites.Count);

        state.HighWriteError = 50;
        state.VcpMaximum = 0;
        try { device.SetBrightness(70); throw new Exception("An empty raw range was accepted."); }
        catch (InvalidOperationException exception)
        {
            Equal(true, exception.Message.Contains("empty DDC/CI brightness range"));
        }
        Equal(0, state.VcpWrites.Count);
        state.VcpMaximum = 250;
        Equal(60, device.SetBrightness(60));
        Equal("150", string.Join(',', state.VcpWrites));
        return Task.CompletedTask;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
