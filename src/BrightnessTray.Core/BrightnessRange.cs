namespace BrightnessTray.Core;

public readonly record struct BrightnessRange
{
    public BrightnessRange(uint minimum, uint maximum)
    {
        if (maximum <= minimum)
            throw new ArgumentOutOfRangeException(nameof(maximum), "The brightness range is empty.");
        Minimum = minimum;
        Maximum = maximum;
    }

    public uint Minimum { get; }
    public uint Maximum { get; }

    public int ToPercent(uint value) => (int)Math.Round(
        ((double)Math.Clamp(value, Minimum, Maximum) - Minimum) * 100 / (Maximum - Minimum),
        MidpointRounding.AwayFromZero);

    public uint FromPercent(int percent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        return (uint)Math.Round(Minimum + (double)(Maximum - Minimum) * percent / 100,
            MidpointRounding.AwayFromZero);
    }
}
