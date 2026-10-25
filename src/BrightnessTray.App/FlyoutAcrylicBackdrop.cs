using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace BrightnessTray.App;

// The panel disappears on deactivation, so its first visible frame must already use active acrylic.
internal sealed partial class FlyoutAcrylicBackdrop : SystemBackdrop
{
    private const float DarkTintOpacity = 0.50f;
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private SystemBackdropConfiguration? _defaultConfiguration;
    private SystemBackdropTheme? _appearanceTheme;
    private bool _isInputActive;
    private bool _isMaterialActive;
    private bool _transparencyEnabled = true;
    private DispatcherQueue? _dispatcherQueue;

    internal event Action? MaterialChanged;
    internal bool IsMaterialActive => _isMaterialActive;
    internal bool TransparencyEnabled => _transparencyEnabled;
    internal bool IsGrainEnabled => _transparencyEnabled && _controller is not null && _configuration?.IsHighContrast == false;

    internal void SetTransparencyEnabled(bool enabled)
    {
        _transparencyEnabled = enabled;
        SetInputActive(_isInputActive);
        NotifyMaterialChanged();
    }

    internal void SetInputActive(bool active)
    {
        _isInputActive = active;
        if (_configuration is not null) _configuration.IsInputActive = active && _transparencyEnabled;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (!DesktopAcrylicController.IsSupported()) return;
        DispatcherQueue.GetForCurrentThread().EnsureSystemDispatcherQueue();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _defaultConfiguration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        _configuration = new SystemBackdropConfiguration { IsInputActive = _isInputActive && _transparencyEnabled };
        CopySystemPolicy();
        _controller = new DesktopAcrylicController();
        _controller.StateChanged += Controller_StateChanged;
        _controller.SetSystemBackdropConfiguration(_configuration);
        ApplyAppearance();
        _controller.AddSystemBackdropTarget(connectedTarget);
        _isMaterialActive = _controller.State == SystemBackdropState.Active;
        NotifyMaterialChanged();
    }

    private void Controller_StateChanged(ISystemBackdropControllerWithTargets sender, object args)
    {
        _isMaterialActive = sender.State == SystemBackdropState.Active;
        NotifyMaterialChanged();
    }

    private void NotifyMaterialChanged() => _dispatcherQueue?.TryEnqueue(() => MaterialChanged?.Invoke());

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        // WinUI can notify during teardown without a valid target; the cached configuration stays readable.
        CopySystemPolicy();
    }

    private void CopySystemPolicy()
    {
        if (_configuration is null || _defaultConfiguration is null) return;
        _configuration.Theme = _defaultConfiguration.Theme;
        _configuration.IsHighContrast = _defaultConfiguration.IsHighContrast;
        _configuration.HighContrastBackgroundColor = _defaultConfiguration.HighContrastBackgroundColor;
        ApplyAppearance();
        NotifyMaterialChanged();
    }

    private void ApplyAppearance()
    {
        if (_controller is null || _configuration is null || _appearanceTheme == _configuration.Theme) return;
        // A custom tint disables automatic theme recipes; keep each theme's other native values explicit.
        var dark = _configuration.Theme == SystemBackdropTheme.Dark;
        var tint = dark ? (byte)44 : (byte)252;
        var fallback = dark ? (byte)44 : (byte)249;
        _controller.TintColor = global::Windows.UI.Color.FromArgb(255, tint, tint, tint);
        _controller.TintOpacity = dark ? DarkTintOpacity : 0f;
        _controller.LuminosityOpacity = dark ? 0.96f : 0.85f;
        _controller.FallbackColor = global::Windows.UI.Color.FromArgb(255, fallback, fallback, fallback);
        _appearanceTheme = _configuration.Theme;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        var controller = _controller;
        // Invalidate our state before WinUI disconnects and can issue reentrant notifications.
        _controller = null;
        _appearanceTheme = null;
        _isMaterialActive = false;
        _configuration = null;
        _defaultConfiguration = null;
        if (controller is not null) controller.StateChanged -= Controller_StateChanged;
        NotifyMaterialChanged();
        try { base.OnTargetDisconnected(disconnectedTarget); }
        finally
        {
            try { controller?.RemoveSystemBackdropTarget(disconnectedTarget); }
            finally { controller?.Dispose(); }
        }
    }
}
