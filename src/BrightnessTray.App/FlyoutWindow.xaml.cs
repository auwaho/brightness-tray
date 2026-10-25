using System;
using System.Diagnostics;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;
using BrightnessTray.App.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;
using Forms = System.Windows.Forms;

namespace BrightnessTray.App;

public sealed partial class FlyoutWindow : Window
{
    internal const int AnimationDurationMs = 80;
    private bool _allowClose;
    private bool _preparing;
    private bool _closing;
    private readonly bool _requireForeground;
    private readonly FlyoutAcrylicBackdrop _backdrop = new();
    private readonly FlyoutGrain _grain;
    private readonly UISettings _uiSettings = new();
    private Task? _showTask;
    private Task? _hideTask;
    private PointInt32 _openPosition;
    private int _showGeneration;
    private long? _taskbarDismissedAt;
    private System.Drawing.Point _taskbarDismissedPosition;
    private EventHandler<object>? _slideFrame;
    private TaskCompletionSource? _slideCompletion;
    private RectInt32 _slideDestination;
    private EventHandler<object>? _layoutFrame;
    private TaskCompletionSource? _layoutCompletion;
    private bool _resizeQueued;
    public FlyoutViewModel ViewModel { get; }

    public FlyoutWindow(FlyoutViewModel viewModel) : this(viewModel, true) { }

    internal FlyoutWindow(FlyoutViewModel viewModel, bool requireForeground)
    {
        _requireForeground = requireForeground;
        InitializeComponent();
        // DWM rounds the outer window; composition keeps the moving content's edges antialiased.
        foreach (var element in new UIElement[] { FlyoutRoot, SurfaceBorder, LayoutRoot, PanelBackdrop, SurfaceStroke })
            ElementCompositionPreview.GetElementVisual(element).BorderMode = Microsoft.UI.Composition.CompositionBorderMode.Soft;
        FlyoutRoot.SizeChanged += (_, eventArgs) => FlyoutRoot.Clip = new RectangleGeometry
        {
            Rect = new global::Windows.Foundation.Rect(0, 0, eventArgs.NewSize.Width, eventArgs.NewSize.Height),
        };
        ViewModel = viewModel;
        LayoutRoot.DataContext = viewModel;
        ViewModel.Displays.CollectionChanged += DisplayRowsChanged;
        ViewModel.PropertyChanged += ViewModelChanged;
        _grain = new FlyoutGrain(GrainHost);
        _backdrop.MaterialChanged += UpdateBackdropAppearance;
        FlyoutRoot.ActualThemeChanged += (_, _) => UpdateBackdropAppearance();
        FlyoutRoot.Loaded += (_, _) => UpdateBackdropAppearance();
        _uiSettings.AdvancedEffectsEnabledChanged += TransparencySettingsChanged;
        _backdrop.SetTransparencyEnabled(_uiSettings.AdvancedEffectsEnabled);
        SystemBackdrop = new TransparentWindowBackdrop();
        PanelBackdrop.SystemBackdrop = _backdrop;
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        NativeWindowPosition.ConfigureFlyoutFrame(WindowHandle);
        Activated += (_, eventArgs) =>
        {
            if (eventArgs.WindowActivationState == WindowActivationState.Deactivated)
                DismissOnDeactivation(NativeWindowPosition.IsTaskbarPointerDown);
        };
        AppWindow.Closing += (_, eventArgs) => { if (!_allowClose) { eventArgs.Cancel = true; Hide(); } };
        Closed += (_, _) =>
        {
            ViewModel.Displays.CollectionChanged -= DisplayRowsChanged;
            ViewModel.PropertyChanged -= ViewModelChanged;
            StopSlide(); StopLayoutWait();
            _backdrop.MaterialChanged -= UpdateBackdropAppearance;
            _uiSettings.AdvancedEffectsEnabledChanged -= TransparencySettingsChanged;
            _grain.Dispose();
        };
    }

    private void UpdateBackdropAppearance()
    {
        if (_allowClose) return;
        _grain.SetMaterial(_backdrop.IsGrainEnabled, FlyoutRoot.ActualTheme);
        GrainHost.Opacity = _backdrop.TransparencyEnabled ? 1 : 0;
        OpaqueBackdrop.Visibility = _backdrop.TransparencyEnabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TransparencySettingsChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_allowClose) _backdrop.SetTransparencyEnabled(_uiSettings.AdvancedEffectsEnabled);
        });

    public bool IsVisible => AppWindow.IsVisible && !_preparing;
    internal IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);
    internal bool AnimationsEnabled => _uiSettings.AnimationsEnabled;
    public void Hide() => HideCore();

    private void HideCore(bool clearTaskbarDismissal = true)
    {
        var wasMenuOpen = IsTrayMenuVisible;
        if (clearTaskbarDismissal) _taskbarDismissedAt = null;
        _showGeneration++;
        _closing = false;
        _preparing = false;
        AppWindow.Hide();
        StopSlide(snapToDestination: false);
        StopLayoutWait();
        NativeWindowPosition.SetCloaked(WindowHandle, false);
        _backdrop.SetInputActive(false);
        if (wasMenuOpen) TrayMenuClosed?.Invoke();
    }

    public async void ShowNearTray()
    {
        await ShowNearTrayAsync();
    }

    internal Task ShowNearTrayAsync()
    {
        SetTrayMenuMode(false);
        return ShowCurrentAsync();
    }

    private Task ShowCurrentAsync()
    {
        _taskbarDismissedAt = null;
        if (_closing)
        {
            _closing = false;
            _showGeneration++;
            StopSlide(snapToDestination: false);
            Activate();
            var foregroundAcquired = NativeWindowPosition.TrySetForeground(WindowHandle);
            if (!foregroundAcquired && _requireForeground) { Hide(); return Task.CompletedTask; }
            _showTask = SlideAsync(new RectInt32(_openPosition.X, _openPosition.Y,
                AppWindow.Size.Width, AppWindow.Size.Height));
            return _showTask;
        }
        if (_showTask is { IsCompleted: false } && (_preparing || IsVisible)) return _showTask;
        if (IsVisible)
        {
            Activate();
            var foregroundAcquired = NativeWindowPosition.TrySetForeground(WindowHandle);
            if (!foregroundAcquired && _requireForeground) Hide();
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _showTask = completion.Task;
        _ = CompleteShowAsync(++_showGeneration, completion);
        return completion.Task;
    }

    internal Task ToggleFromTrayAsync()
    {
        if (_showingTrayMenu) return ShowNearTrayAsync();
        // A tray press can start dismissing the panel before the shell callback delivers MouseDown.
        var dismissedByThisPress = _taskbarDismissedAt is { } timestamp
            && Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds < 250
            && Math.Abs(Forms.Cursor.Position.X - _taskbarDismissedPosition.X) <= 4
            && Math.Abs(Forms.Cursor.Position.Y - _taskbarDismissedPosition.Y) <= 4;
        _taskbarDismissedAt = null;
        if (dismissedByThisPress) return _hideTask ?? Task.CompletedTask;
        if (_closing) return ShowNearTrayAsync();
        if (IsVisible || _preparing) return HideWithAnimationAsync();
        return ShowNearTrayAsync();
    }

    internal void DismissOnDeactivation(bool taskbarPointerDown)
    {
        if (_closing || !IsVisible && !_preparing) return;
        if (taskbarPointerDown)
        {
            _ = HideWithAnimationAsync();
            _taskbarDismissedAt = Stopwatch.GetTimestamp();
            _taskbarDismissedPosition = Forms.Cursor.Position;
        }
        else _ = HideWithAnimationAsync();
    }

    private Task HideWithAnimationAsync()
    {
        if (_closing) return _hideTask ?? Task.CompletedTask;
        if (_preparing || !IsVisible || !AnimationsEnabled) { Hide(); return _hideTask = Task.CompletedTask; }
        _closing = true;
        var generation = ++_showGeneration;
        StopSlide(snapToDestination: false);
        StopLayoutWait();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _hideTask = completion.Task;
        _ = CompleteHideAsync(generation, completion);
        return completion.Task;
    }

    private async Task CompleteHideAsync(int generation, TaskCompletionSource completion)
    {
        try
        {
            var scale = NativeWindowPosition.GetScale(WindowHandle);
            var size = AppWindow.Size;
            var destination = new RectInt32(_openPosition.X,
                _openPosition.Y + size.Height + (int)Math.Ceiling(12 * scale), size.Width, size.Height);
            await SlideAsync(destination);
            // A newer tray press can reverse the motion before this continuation runs.
            if (generation == _showGeneration && _closing) HideCore(clearTaskbarDismissal: false);
        }
        catch (Exception exception)
        {
            if (generation == _showGeneration) Hide();
            completion.TrySetException(exception);
        }
        finally { completion.TrySetResult(); }
    }

    private async Task CompleteShowAsync(int generation, TaskCompletionSource completion)
    {
        try
        {
            // Claim focus during the tray callback; DWM conceals the window until its data/layout are ready.
            _preparing = true;
            NativeWindowPosition.SetCloaked(WindowHandle, true);
            _backdrop.SetInputActive(true);
            AppWindow.Show();
            Activate();
            var foregroundAcquired = NativeWindowPosition.TrySetForeground(WindowHandle);
            // Light-dismiss requires the flyout to acquire focus.
            if (!foregroundAcquired && _requireForeground) { Hide(); return; }
            // Populate and measure while hidden: no stale editable rows or intermediate visible sizes.
            if (_showingTrayMenu)
            {
                if (_startupMenuItem is not null) await _startupMenuItem.RefreshAsync();
                UpdateStartupMenu();
            }
            else await ViewModel.RefreshAsync();
            if (generation != _showGeneration || _allowClose) return;
            await WaitForLayoutFrameAsync();
            if (generation != _showGeneration || _allowClose) return;
            var bounds = MeasureNearTray();
            AppWindow.MoveAndResize(bounds);
            // Native resize reaches XAML on the next frame; do not animate its old, default-sized layout.
            await WaitForLayoutFrameAsync();
            if (generation != _showGeneration || _allowClose) return;
            FlyoutRoot.UpdateLayout();
            var scale = NativeWindowPosition.GetScale(WindowHandle);
            var area = Forms.Screen.FromHandle(WindowHandle).WorkingArea;
            var offset = AnimationsEnabled ? bounds.Height + (int)Math.Ceiling(12 * scale) : 0;
            _openPosition = new PointInt32(bounds.X, bounds.Y);
            AppWindow.Move(new PointInt32(bounds.X, bounds.Y + offset));
            NativeWindowPosition.ConfigureFlyoutFrame(WindowHandle);
            NativeWindowPosition.KeepBelowTaskbar(WindowHandle, bounds.X + bounds.Width / 2, area.Bottom + 1);
            _preparing = false;
            NativeWindowPosition.SetCloaked(WindowHandle, false);
            if (generation == _showGeneration && IsVisible) await SlideAsync(bounds);
            if (generation == _showGeneration && IsTrayMenuVisible) MenuOpenButton.Focus(FocusState.Programmatic);
        }
        catch (Exception exception)
        {
            if (generation == _showGeneration) Hide();
            completion.TrySetException(exception);
        }
        finally { completion.TrySetResult(); }
    }

    private RectInt32 MeasureNearTray()
    {
        var area = Forms.Screen.FromPoint(_showingTrayMenu ? _menuAnchor : Forms.Cursor.Position).WorkingArea;
        // This move selects the target monitor's DPI; it must happen before the window is visible.
        AppWindow.Move(new PointInt32(Math.Max(area.Left, area.Right - 400), Math.Max(area.Top, area.Bottom - 300)));
        var scale = NativeWindowPosition.GetScale(WindowHandle);
        FlyoutRoot.UpdateLayout();
        FlyoutRoot.Measure(new global::Windows.Foundation.Size(FlyoutRoot.Width, double.PositiveInfinity));
        var width = (int)Math.Ceiling(FlyoutRoot.Width * scale);
        var margin = (int)Math.Ceiling(12 * scale);
        var height = Math.Min((int)Math.Ceiling(FlyoutRoot.DesiredSize.Height * scale),
            Math.Max(1, area.Height - 2 * margin));
        if (_showingTrayMenu)
            return new RectInt32(Math.Max(area.Left + margin, Math.Min(_menuAnchor.X - width / 2, area.Right - width - margin)),
                Math.Max(area.Top + margin, Math.Min(_menuAnchor.Y, area.Bottom - margin) - height), width, height);
        return new RectInt32(Math.Max(area.Left + margin, area.Right - width - margin),
            Math.Max(area.Top + margin, area.Bottom - height - margin), width, height);
    }

    private Task WaitForLayoutFrameAsync()
    {
        // Item templates/bindings settle on a frame before the hidden window is measured.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _layoutCompletion = completion;
        _layoutFrame = (_, _) => StopLayoutWait();
        CompositionTarget.Rendering += _layoutFrame;
        return completion.Task;
    }

    private void StopLayoutWait()
    {
        if (_layoutFrame is null) return;
        CompositionTarget.Rendering -= _layoutFrame;
        _layoutFrame = null;
        _layoutCompletion?.TrySetResult();
        _layoutCompletion = null;
    }

    private Task SlideAsync(RectInt32 destination)
    {
        StopSlide(snapToDestination: false);
        var offset = AppWindow.Position.Y - destination.Y;
        _slideDestination = destination;
        if (offset == 0 || !AnimationsEnabled)
        {
            AppWindow.Move(new PointInt32(destination.X, destination.Y));
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _slideCompletion = completion;
        var start = Stopwatch.GetTimestamp();
        _slideFrame = (_, _) =>
        {
            var remaining = FlyoutAnimation.Remaining(Stopwatch.GetElapsedTime(start).TotalMilliseconds, AnimationDurationMs);
            AppWindow.Move(new PointInt32(destination.X, destination.Y + (int)Math.Round(offset * remaining)));
            if (remaining <= 0) StopSlide();
        };
        CompositionTarget.Rendering += _slideFrame;
        return completion.Task;
    }

    private void StopSlide(bool snapToDestination = true)
    {
        if (_slideFrame is null) return;
        CompositionTarget.Rendering -= _slideFrame;
        _slideFrame = null;
        if (snapToDestination) AppWindow.Move(new PointInt32(_slideDestination.X, _slideDestination.Y));
        _slideCompletion?.TrySetResult();
        _slideCompletion = null;
    }

    public void CloseForExit() { _allowClose = true; Hide(); Close(); }

    internal async Task RefreshOrShowAsync()
    {
        if (!IsVisible) { await ShowNearTrayAsync(); return; }
        await RefreshVisibleAsync();
    }

    private async Task RefreshVisibleAsync()
    {
        await ViewModel.RefreshAsync();
        if (_showTask is { IsCompleted: false }) await _showTask;
        ResizeVisibleFlyout();
    }

    private void DisplayRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueResize();

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlyoutViewModel.IsRefreshing) && !ViewModel.IsRefreshing) QueueResize();
    }

    private void QueueResize()
    {
        if (_showingTrayMenu || _resizeQueued || _allowClose || !IsVisible || _closing || _preparing || ViewModel.IsRefreshing) return;
        _resizeQueued = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _resizeQueued = false;
            if (!ViewModel.IsRefreshing && _showTask is not { IsCompleted: false }) ResizeVisibleFlyout();
        });
    }

    private void ResizeVisibleFlyout()
    {
        if (_showingTrayMenu || !IsVisible || _closing) return;
        // Keep the tray anchor while resizing; never visit the temporary DPI-selection position here.
        FlyoutRoot.UpdateLayout();
        FlyoutRoot.Measure(new global::Windows.Foundation.Size(FlyoutRoot.Width, double.PositiveInfinity));
        var scale = NativeWindowPosition.GetScale(WindowHandle);
        var area = Forms.Screen.FromHandle(WindowHandle).WorkingArea;
        var margin = (int)Math.Ceiling(12 * scale);
        var width = AppWindow.Size.Width;
        var height = Math.Min((int)Math.Ceiling(FlyoutRoot.DesiredSize.Height * scale),
            Math.Max(1, area.Height - 2 * margin));
        if (AppWindow.Size.Height == height) return;
        _openPosition = new PointInt32(AppWindow.Position.X, Math.Max(area.Top + margin, area.Bottom - height - margin));
        AppWindow.MoveAndResize(new RectInt32(_openPosition.X, _openPosition.Y, width, height));
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshVisibleAsync();
    }
    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { Hide(); e.Handled = true; }
    }
}
