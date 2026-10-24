using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace BrightnessTray.App.ViewModels;

internal sealed class TrayBrightnessController(FlyoutViewModel flyout, Func<bool>? canRefresh = null) : IAsyncDisposable
{
    private const int WheelDelta = 120;
    private const int StepPercent = 5;
    private readonly Dictionary<string, Target> _targets = [];
    private long _pendingDelta;
    private long? _lastScroll;
    private bool _refreshRequested;
    private bool _disposed;
    private Task? _worker;

    internal Task ScrollAsync(int delta)
    {
        if (_disposed || delta == 0) return Task.CompletedTask;
        _pendingDelta += delta;
        var now = Stopwatch.GetTimestamp();
        if (_lastScroll is null || Stopwatch.GetElapsedTime(_lastScroll.Value, now) > TimeSpan.FromSeconds(1))
            _refreshRequested = true;
        _lastScroll = now;
        if (_worker is { IsCompleted: false }) return _worker;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _worker = completion.Task;
        _ = DrainAsync(completion);
        return completion.Task;
    }

    private async Task DrainAsync(TaskCompletionSource completion)
    {
        try
        {
            while (!_disposed && _pendingDelta != 0)
            {
                if (_refreshRequested)
                {
                    _refreshRequested = false;
                    // Re-read hidden-panel gestures; an open panel already has current editable rows.
                    if (canRefresh?.Invoke() != false) await RefreshAsync();
                }
                // Still join an explicit refresh so writes never use rows that are being replaced.
                if (flyout.IsRefreshing) await RefreshAsync();
                if (_disposed) break;
                var steps = _pendingDelta / WheelDelta;
                _pendingDelta %= WheelDelta;
                if (steps == 0) break;
                var change = (int)Math.Clamp(steps, -20L, 20L) * StepPercent;
                foreach (var id in flyout.Displays.Select(display => display.Id).ToArray())
                {
                    if (flyout.IsRefreshing) await RefreshAsync();
                    if (_disposed) break;
                    var display = flyout.Displays.FirstOrDefault(row => row.Id == id);
                    if (display is null || !display.IsAvailable) continue;
                    if (!_targets.TryGetValue(display.Id, out var target) || !ReferenceEquals(target.Row, display)
                        || target.EditVersion != display.EditVersion)
                        target = new Target(display, (int)display.Brightness, display.EditVersion);
                    // Preserve requested progress when a driver only supports coarse brightness levels.
                    var value = Math.Clamp(target.Value + change, 0, 100);
                    var editVersion = display.EditVersion + (value != display.Brightness ? 1 : 0);
                    _targets[display.Id] = new Target(display, value, editVersion);
                    display.Brightness = value;
                }
            }
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
        finally { _worker = null; }
    }

    private async Task RefreshAsync()
    {
        await flyout.RefreshAsync();
        // Preserve coarse-driver progress when hardware still confirms our previous value.
        foreach (var id in _targets.Keys.ToArray())
        {
            var target = _targets[id];
            var row = flyout.Displays.FirstOrDefault(display => display.Id == id);
            if (row is not null && target.Row.EditVersion == target.EditVersion
                && row.Brightness == target.Row.ConfirmedBrightness)
                _targets[id] = target with { Row = row, EditVersion = row.EditVersion };
            else _targets.Remove(id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _pendingDelta = 0;
        if (_worker is not null) await _worker;
        _targets.Clear();
    }

    private sealed record Target(DisplayViewModel Row, int Value, long EditVersion);
}
