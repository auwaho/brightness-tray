using System;
using System.Threading.Tasks;
using BrightnessTray.Core;
using BrightnessTray.Core.Displays;

namespace BrightnessTray.App.ViewModels;

public sealed class DisplayViewModel : ObservableObject, IAsyncDisposable
{
    private readonly BrightnessService _service;
    private Task? _worker;
    private int? _pending;
    private int _confirmed;
    private int _brightness;
    private bool _isAvailable;
    private bool _isPaused;
    private bool _disposed;
    private string? _error;
    private long _wheelDelta;
    private int _wheelTarget;
    private long _wheelEditVersion = -1;

    public DisplayViewModel(DisplaySnapshot display, BrightnessService service)
    {
        _service = service;
        Id = display.Id;
        Name = display.Name;
        ConnectionLabel = display.ConnectionLabel;
        _brightness = _confirmed = display.BrightnessPercent ?? 0;
        _isAvailable = display.BrightnessPercent.HasValue;
        _error = display.Error;
    }

    public string Id { get; }
    public string Name { get; }
    public string ConnectionLabel { get; }
    internal long EditVersion { get; private set; }
    internal int ConfirmedBrightness => _confirmed;
    internal bool IsControllable => _isAvailable;
    public bool IsAvailable => _isAvailable && !_isPaused && !_disposed;
    public string ValueLabel => _isAvailable ? $"{_brightness}%" : "—";
    public string? UnavailableReason => _isAvailable ? null
        : _error ?? "Brightness control is unavailable. Refresh the display list.";

    public double Brightness
    {
        get => _brightness;
        set
        {
            if (!IsAvailable) return;
            var percent = (int)Math.Round(Math.Clamp(value, 0, 100));
            if (_brightness == percent) return;
            _brightness = percent;
            _pending = percent;
            EditVersion++;
            // A binding listener can start refresh or exit while processing these notifications.
            _worker ??= ApplyPendingAsync();
            Notify();
            Notify(nameof(ValueLabel));
        }
    }

    internal bool AdjustBrightnessFromWheel(int delta)
    {
        if (!IsAvailable || delta == 0) return false;
        if (_wheelEditVersion != EditVersion)
        {
            _wheelDelta = 0;
            _wheelTarget = _brightness;
            _wheelEditVersion = EditVersion;
        }
        _wheelDelta += delta;
        var steps = _wheelDelta / 120;
        _wheelDelta %= 120;
        if (steps == 0) return true;
        // Keep the requested level when a driver rounds a five-point step to a coarser level.
        _wheelTarget = Math.Clamp(_wheelTarget + (int)Math.Clamp(steps, -20L, 20L) * 5, 0, 100);
        _wheelEditVersion = EditVersion + (_wheelTarget != _brightness ? 1 : 0);
        Brightness = _wheelTarget;
        return true;
    }

    private async Task ApplyPendingAsync()
    {
        try
        {
            while (_pending.HasValue)
            {
                // Coalesce drag events and always send the final requested value.
                await Task.Delay(120);
                var requested = _pending.Value;
                _pending = null;
                try
                {
                    _confirmed = await _service.SetBrightnessAsync(Id, requested);
                    _error = null;
                    if (!_pending.HasValue) SetDisplayedValue(_confirmed);
                }
                catch (Exception exception)
                {
                    _pending = null;
                    _error = $"Could not apply brightness: {exception.Message} Refresh the display list.";
                    _isAvailable = false;
                    // Rollback notifications may reenter the setter; reject edits before notifying listeners.
                    SetDisplayedValue(_confirmed);
                    Notify(nameof(IsAvailable));
                    Notify(nameof(ValueLabel));
                    Notify(nameof(UnavailableReason));
                    Notify(nameof(IsControllable));
                }
            }
        }
        finally { _worker = null; }
    }

    private void SetDisplayedValue(int value)
    {
        _brightness = value;
        Notify(nameof(Brightness));
        Notify(nameof(ValueLabel));
    }

    internal Task PauseAndDrainAsync()
    {
        // Refresh and exit must finish the user's last selection before releasing native handles.
        if (!_isPaused)
        {
            _isPaused = true;
            Notify(nameof(IsAvailable));
        }
        return _worker ?? Task.CompletedTask;
    }

    internal void Resume()
    {
        if (_disposed || !_isPaused) return;
        _isPaused = false;
        Notify(nameof(IsAvailable));
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return new ValueTask(PauseAndDrainAsync());
    }
}
