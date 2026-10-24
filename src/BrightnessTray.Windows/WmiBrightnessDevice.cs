using BrightnessTray.Core.Displays;

namespace BrightnessTray.Windows;

internal sealed class WmiBrightnessDevice(string instanceName, string name, byte[] levels) : IBrightnessDevice
{
    private bool _disposed;

    public string Id => "wmi:" + instanceName;
    public string Name { get; } = name;
    public string ConnectionLabel => "Built-in display · WMI";
    internal string InstanceName => instanceName;

    public int ReadBrightness()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureAttached();
        if (levels.Length == 0)
            throw new InvalidOperationException("The driver reported an invalid WMI brightness range.");
        using var session = new WmiSession();
        int? brightness = null;
        session.Query("SELECT InstanceName, CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE", item =>
        {
            if (!Matches(item)) return;
            var current = WmiSession.ReadValue(item, "CurrentBrightness");
            if (current is null or DBNull || Convert.ToInt32(current) is < 0 or > 100)
                throw new InvalidOperationException("The driver reported an invalid WMI brightness value.");
            brightness = Convert.ToInt32(current);
        });
        if (brightness is null)
            throw new InvalidOperationException("The built-in display is not active. Turn it on, then refresh the display list.");

        var writable = false;
        session.Query("SELECT InstanceName FROM WmiMonitorBrightnessMethods WHERE Active = TRUE", item =>
        {
            if (Matches(item)) writable = true;
        });
        if (!writable)
            throw new InvalidOperationException("The built-in display brightness controls are not active. Turn on the display, then refresh the display list.");
        session.ValidateBrightnessControl();
        return brightness.Value;
    }

    public int SetBrightness(int percent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        EnsureAttached();
        if (levels.Length == 0)
            throw new InvalidOperationException("The driver reported an invalid WMI brightness range.");
        var applied = levels.MinBy(level => Math.Abs(level - percent));
        using var session = new WmiSession();
        var found = false;
        session.Query("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE", item =>
        {
            if (!Matches(item)) return;
            session.SetBrightness(item, applied);
            found = true;
        });
        if (!found)
            throw new InvalidOperationException("The built-in display brightness driver is no longer available. Refresh the display list.");
        return applied;
    }

    public void Dispose() => _disposed = true;

    private void EnsureAttached()
    {
        if (WindowsDisplayDiscovery.IsDisplayAttached(instanceName) == false)
            throw new InvalidOperationException("The built-in display is disconnected or disabled in Windows. Enable it, then refresh the display list.");
    }

    private bool Matches(object item) => string.Equals(instanceName,
        WmiSession.ReadValue(item, "InstanceName") as string, StringComparison.OrdinalIgnoreCase);
}
