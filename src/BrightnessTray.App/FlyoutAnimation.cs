using System;

namespace BrightnessTray.App;

internal static class FlyoutAnimation
{
    // Shared by both directions and reversals; the signed offset determines movement direction.
    internal static double Remaining(double elapsedMilliseconds, int durationMilliseconds) =>
        1 - Math.Min(1, elapsedMilliseconds / durationMilliseconds);
}
