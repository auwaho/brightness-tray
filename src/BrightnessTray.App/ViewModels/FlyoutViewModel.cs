using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using BrightnessTray.Core;
using BrightnessTray.Core.Displays;

namespace BrightnessTray.App.ViewModels;

public sealed class FlyoutViewModel(BrightnessService service) : ObservableObject, IAsyncDisposable
{
    private bool _isRefreshing;
    private bool _disposed;
    private string _status = "Refresh to find displays.";
    private Task? _refreshTask;
    // Failed rows leave the visible collection, but their workers still need draining on refresh/exit.
    private readonly List<DisplayViewModel> _rows = [];
    public ObservableCollection<DisplayViewModel> Displays { get; } = [];
    public ObservableCollection<DisplayIssue> Issues { get; } = [];
    public bool IsRefreshing => _isRefreshing;
    public bool CanRefresh => !_isRefreshing && !_disposed;
    public string Status => _status;

    public Task RefreshAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_refreshTask is { IsCompleted: false }) return _refreshTask;
        // Publish the operation before notifications can reenter refresh or disposal.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshTask = completion.Task;
        _ = CompleteRefreshAsync(completion);
        return completion.Task;
    }

    private async Task CompleteRefreshAsync(TaskCompletionSource completion)
    {
        try
        {
            await RefreshCoreAsync();
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    private async Task RefreshCoreAsync()
    {
        _isRefreshing = true;
        _status = "Finding displays…";
        Notify(nameof(IsRefreshing));
        Notify(nameof(CanRefresh));
        Notify(nameof(Status));
        var previousDisplays = _rows.ToArray();
        try
        {
            // Start every pause before awaiting so no row accepts edits while discovery is pending.
            await Task.WhenAll(previousDisplays.Select(display => display.PauseAndDrainAsync()));
            if (_disposed) return;
            var result = await service.RefreshAsync();
            if (_disposed) return;
            foreach (var display in previousDisplays)
            {
                display.PropertyChanged -= DisplayPropertyChanged;
                await display.DisposeAsync();
            }
            _rows.Clear();
            Displays.Clear();
            Issues.Clear();
            foreach (var display in result.Displays.Where(display => display.BrightnessPercent.HasValue))
            {
                var row = new DisplayViewModel(display, service);
                _rows.Add(row);
                row.PropertyChanged += DisplayPropertyChanged;
                Displays.Add(row);
            }
            foreach (var issue in result.Issues) Issues.Add(issue);
            _status = Displays.Count == 0
                ? "No controllable displays found. For an external monitor, check DDC/CI in its menu."
                : string.Empty;
        }
        catch (Exception exception)
        {
            if (!_disposed)
                foreach (var display in previousDisplays) display.Resume();
            _status = $"Could not find displays: {exception.Message}";
        }
        finally
        {
            _isRefreshing = false;
            Notify(nameof(IsRefreshing));
            Notify(nameof(CanRefresh));
            Notify(nameof(Status));
        }
    }

    private void DisplayPropertyChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName != nameof(DisplayViewModel.IsControllable)
            || sender is not DisplayViewModel row || row.IsControllable) return;
        row.PropertyChanged -= DisplayPropertyChanged;
        Displays.Remove(row);
        if (!_isRefreshing && !_disposed && Displays.Count == 0)
        {
            _status = "No controllable displays found. Turn on a display, then refresh the list.";
            Notify(nameof(Status));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            Notify(nameof(CanRefresh));
        }
        if (_refreshTask is not null) await _refreshTask;
        foreach (var row in _rows) row.PropertyChanged -= DisplayPropertyChanged;
        await Task.WhenAll(_rows.Select(display => display.DisposeAsync().AsTask()));
    }
}
