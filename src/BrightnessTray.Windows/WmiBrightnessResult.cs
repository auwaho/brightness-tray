namespace BrightnessTray.Windows;

internal static class WmiBrightnessResult
{
    internal static void Validate(bool declaresOutput, object? output, Func<object, string, object?> readValue)
    {
        // Some real WMI providers declare WmiSetBrightness with no OutParameters at all.
        // For those providers the COM call succeeding is the acknowledgement.
        if (output is null && !declaresOutput) return;
        if (output is null)
            throw new InvalidOperationException("The driver did not confirm the WMI brightness change.");
        var result = readValue(output, "ReturnValue");
        if (result is null or DBNull)
            throw new InvalidOperationException("The driver did not confirm the WMI brightness change.");
        var status = Convert.ToUInt32(result);
        if (status != 0)
            throw new InvalidOperationException($"The driver rejected the WMI brightness change (code {status}).");
    }
}
