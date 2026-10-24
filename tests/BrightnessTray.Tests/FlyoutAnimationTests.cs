using BrightnessTray.App;

internal static class FlyoutAnimationTests
{
    internal static Task LinearInterpolationAsync()
    {
        // The UI calls this production helper for opening, closing and partial reversals.
        foreach (var sample in new (double Elapsed, double Remaining)[]
        {
            (-8, 1.1), (0, 1), (8, 0.9), (20, 0.75), (32, 0.6), (40, 0.5), (48, 0.4), (60, 0.25), (72, 0.1), (80, 0), (120, 0),
        }) Equal(sample.Remaining, FlyoutAnimation.Remaining(sample.Elapsed, 80));
        // A shorter remaining distance still uses the same linear fraction and duration.
        Equal(0.75, FlyoutAnimation.Remaining(25, 100));
        Equal(0.25, FlyoutAnimation.Remaining(75, 100));
        return Task.CompletedTask;
    }

    internal static Task SparseFramesAsync()
    {
        // At 60 Hz this valid phase misses the complete 40–60% progress band.
        // It used to fail the UI smoke despite perfectly linear movement.
        double[] frameTimes = [15, 31.6667, 48.3334, 65.0001, 81.6668];
        var remaining = frameTimes.Select(time => FlyoutAnimation.Remaining(time, 80)).ToArray();
        if (remaining.Any(value => value is >= 0.4 and <= 0.6))
            throw new InvalidOperationException("The regression no longer exercises a missing midpoint frame.");
        Equal(0.8125, remaining[0]);
        Equal(0.60416625, remaining[1]);
        Equal(0.3958325, remaining[2]);
        Equal(0.18749875, remaining[3]);
        Equal(0, remaining[4]);
        if (remaining.Zip(remaining.Skip(1)).Any(pair => pair.First < pair.Second))
            throw new InvalidOperationException("Sparse-frame animation reversed direction.");
        // A stalled renderer may only observe the start and final frame. The curve
        // still lands exactly on the endpoint and never extrapolates past it.
        Equal(1, FlyoutAnimation.Remaining(0, 80));
        Equal(0, FlyoutAnimation.Remaining(130, 80));
        return Task.CompletedTask;
    }

    private static void Equal(double expected, double actual)
    {
        if (Math.Abs(expected - actual) > 0.0000001)
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
