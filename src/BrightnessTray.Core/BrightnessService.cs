using BrightnessTray.Core.Displays;

namespace BrightnessTray.Core;

public sealed class BrightnessService(IDisplayDiscovery discovery) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<IBrightnessDevice> _devices = [];
    private bool _disposed;

    public async Task<DisplayRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                // Discovery owns new handles; retain the old set if discovery itself fails.
                var discovered = discovery.Discover();
                var replacements = discovered.Devices.ToList();
                if (replacements.Any(device => string.IsNullOrWhiteSpace(device.Id)) ||
                    replacements.Select(device => device.Id).Distinct(StringComparer.Ordinal).Count() != replacements.Count)
                {
                    DisposeDevices(replacements);
                    throw new InvalidOperationException("Discovery returned empty or duplicate display identifiers.");
                }
                var issues = discovered.Issues.ToList();
                issues.AddRange(DisposeDevices(_devices));
                _devices = replacements;
                var displays = _devices.Select(device =>
                {
                    try
                    {
                        var brightness = device.ReadBrightness();
                        ValidatePercent(brightness);
                        return new DisplaySnapshot(device.Id, device.Name, device.ConnectionLabel,
                            brightness, null);
                    }
                    catch (Exception exception)
                    {
                        return new DisplaySnapshot(device.Id, device.Name, device.ConnectionLabel,
                            null, exception.Message);
                    }
                }).ToArray();
                return new DisplayRefreshResult(displays, issues);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> SetBrightnessAsync(string deviceId, int percent,
        CancellationToken cancellationToken = default)
    {
        ValidatePercent(percent);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var device = _devices.SingleOrDefault(device => device.Id == deviceId)
                ?? throw new InvalidOperationException("Display is no longer available. Refresh the display list.");
            return await Task.Run(() =>
            {
                var applied = device.SetBrightness(percent);
                ValidatePercent(applied);
                return applied;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            var issues = await Task.Run(() => DisposeDevices(_devices)).ConfigureAwait(false);
            _devices.Clear();
            if (issues.Count > 0)
                throw new AggregateException(issues.Select(issue => new InvalidOperationException(issue.Message)));
        }
        finally { _gate.Release(); }
    }

    private static List<DisplayIssue> DisposeDevices(IEnumerable<IBrightnessDevice> devices)
    {
        var issues = new List<DisplayIssue>();
        var seen = new HashSet<IBrightnessDevice>(ReferenceEqualityComparer.Instance);
        foreach (var device in devices)
        {
            if (!seen.Add(device)) continue;
            try { device.Dispose(); }
            catch (Exception exception) { issues.Add(new DisplayIssue(device.Name, exception.Message)); }
        }
        return issues;
    }

    private static void ValidatePercent(int percent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
    }
}
