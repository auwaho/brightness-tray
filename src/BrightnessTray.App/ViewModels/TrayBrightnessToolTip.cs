using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace BrightnessTray.App.ViewModels;

internal sealed class TrayBrightnessToolTip : IAsyncDisposable
{
    private const string DefaultText = "BrightnessTray · Display brightness";
    private readonly FlyoutViewModel _flyout;
    private readonly Action<string> _setText;
    private readonly HashSet<DisplayViewModel> _rows = [];
    private Task? _refreshTask;
    private long? _lastRefreshAttempt;
    private string? _lastText;
    private bool _hasRefreshed;
    private bool _disposed;

    internal TrayBrightnessToolTip(FlyoutViewModel flyout, Action<string> setText)
    {
        _flyout = flyout;
        _setText = setText;
        _hasRefreshed = flyout.Displays.Count > 0;
        _flyout.Displays.CollectionChanged += DisplaysChanged;
        _flyout.PropertyChanged += FlyoutChanged;
        RebindRows();
        Update();
    }

    internal Task RefreshOnHoverAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_refreshTask is { IsCompleted: false }) return _refreshTask;
        var now = Stopwatch.GetTimestamp();
        if (_lastRefreshAttempt.HasValue
            && Stopwatch.GetElapsedTime(_lastRefreshAttempt.Value, now) < TimeSpan.FromSeconds(1))
            return Task.CompletedTask;
        _lastRefreshAttempt = now;
        // Publish before the view model can notify a reentrant hover or disposal listener.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshTask = completion.Task;
        _ = CompleteRefreshAsync(completion);
        return completion.Task;
    }

    private async Task CompleteRefreshAsync(TaskCompletionSource completion)
    {
        try
        {
            await _flyout.RefreshAsync();
            _hasRefreshed = true;
            Update();
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    private void Update()
    {
        if (_disposed || _flyout.IsRefreshing) return;
        var displays = _flyout.Displays.Where(row => row.IsControllable)
            .Select(row => (row.Name, (int)row.Brightness)).ToArray();
        var text = displays.Length == 0 && !_hasRefreshed ? DefaultText : Format(displays);
        if (_lastText == text) return;
        _lastText = text;
        _setText(text);
    }

    private void DisplaysChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_disposed) return;
        RebindRows();
        Update();
    }

    private void FlyoutChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(FlyoutViewModel.IsRefreshing) || _flyout.IsRefreshing) return;
        _hasRefreshed = true;
        Update();
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DisplayViewModel.Brightness) or nameof(DisplayViewModel.IsControllable)) Update();
    }

    private void RebindRows()
    {
        var current = _flyout.Displays.ToHashSet();
        foreach (var row in _rows.Except(current).ToArray())
        {
            row.PropertyChanged -= RowChanged;
            _rows.Remove(row);
        }
        foreach (var row in current)
            if (_rows.Add(row)) row.PropertyChanged += RowChanged;
    }

    internal static string Format(IReadOnlyList<(string Name, int Brightness)> displays)
    {
        if (displays.Count == 0) return "No controllable displays found.";
        // Shell's szTip stores 128 UTF-16 characters, including the terminating null.
        var shown = Math.Min(displays.Count, 6);
        var more = shown < displays.Count ? $"+{displays.Count - shown} more displays" : null;
        var names = displays.Take(shown).Select(display => CleanName(display.Name)).ToArray();
        var suffixes = displays.Take(shown).Select(display => $": {Math.Clamp(display.Brightness, 0, 100)}%").ToArray();
        var available = 127 - (shown - 1) - suffixes.Sum(suffix => suffix.Length) - (more is null ? 0 : more.Length + 1);
        var budgets = names.Select(name => Math.Min(name.Length, available / shown)).ToArray();
        var spare = available - budgets.Sum();
        for (var i = 0; i < shown; i++)
        {
            var extra = Math.Min(spare, names[i].Length - budgets[i]);
            budgets[i] += extra;
            spare -= extra;
        }
        var lines = names.Select((name, index) => Truncate(name, budgets[index]) + suffixes[index]).ToList();
        if (more is not null) lines.Add(more);
        return string.Join("\n", lines);
    }

    private static string CleanName(string name)
    {
        var clean = new string(name.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        return clean.Length == 0 ? "Display" : clean;
    }

    private static string Truncate(string text, int length)
    {
        if (text.Length <= length) return text;
        var count = Math.Max(0, length - 1);
        if (count > 0 && char.IsHighSurrogate(text[count - 1]) && char.IsLowSurrogate(text[count])) count--;
        return text[..count] + "…";
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _flyout.Displays.CollectionChanged -= DisplaysChanged;
            _flyout.PropertyChanged -= FlyoutChanged;
            foreach (var row in _rows) row.PropertyChanged -= RowChanged;
            _rows.Clear();
        }
        if (_refreshTask is not null) await _refreshTask;
    }
}
