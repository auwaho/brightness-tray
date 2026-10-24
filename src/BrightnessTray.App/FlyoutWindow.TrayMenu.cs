using System;
using System.Threading.Tasks;
using BrightnessTray.App.Startup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace BrightnessTray.App;

public sealed partial class FlyoutWindow
{
    private bool _showingTrayMenu;
    private System.Drawing.Point _menuAnchor;
    private StartupMenuItem? _startupMenuItem;
    private Func<Task>? _exitFromMenu;
    internal bool IsTrayMenuVisible => _showingTrayMenu && (IsVisible || _preparing);
    internal event Action? TrayMenuClosed;

    internal void ConfigureTrayMenu(StartupMenuItem startup, Func<Task> exit)
    {
        _startupMenuItem = startup;
        _exitFromMenu = exit;
    }

    internal Task ShowTrayMenuAsync(System.Drawing.Point? anchor = null)
    {
        _menuAnchor = anchor ?? System.Windows.Forms.Cursor.Position;
        SetTrayMenuMode(true);
        return ShowCurrentAsync();
    }

    private void SetTrayMenuMode(bool menu)
    {
        if (_showingTrayMenu == menu) return;
        Hide();
        _showingTrayMenu = menu;
        FlyoutRoot.Width = LayoutRoot.Width = menu ? 248 : 360;
        TrayMenuPanel.Visibility = menu ? Visibility.Visible : Visibility.Collapsed;
        DisplayScroller.Visibility = FooterPanel.Visibility = menu ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateStartupMenu()
    {
        MenuStartupButton.IsEnabled = _startupMenuItem?.Enabled == true;
        MenuStartupIcon.Opacity = MenuStartupButton.IsEnabled ? 1 : 0.38;
        MenuStartupCheck.Visibility = _startupMenuItem?.Checked == true ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(MenuStartupHost, _startupMenuItem?.ToolTipText);
    }

    private async void TrayMenu_Click(object sender, RoutedEventArgs e) =>
        await RunTrayMenuCommandAsync(((Button)sender).Tag as string);

    private async Task RunTrayMenuCommandAsync(string? command)
    {
        switch (command)
        {
            case "Open": await ShowNearTrayAsync(); break;
            case "Refresh":
                await HideWithAnimationAsync();
                await ViewModel.RefreshAsync();
                break;
            case "Startup":
                if (_startupMenuItem is null) return;
                var change = _startupMenuItem.ToggleAsync();
                UpdateStartupMenu();
                await change;
                UpdateStartupMenu();
                await HideWithAnimationAsync();
                break;
            case "Exit":
                await HideWithAnimationAsync();
                if (_exitFromMenu is not null) await _exitFromMenu();
                break;
        }
    }

    private void TrayMenu_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            _ = HideWithAnimationAsync();
            e.Handled = true;
            return;
        }
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)) return;
        var items = new[] { MenuOpenButton, MenuRefreshButton, MenuStartupButton, MenuExitButton };
        var current = Array.FindIndex(items, item => item.FocusState != FocusState.Unfocused);
        var step = e.Key == VirtualKey.Up ? -1 : 1;
        var next = e.Key == VirtualKey.Home ? 0 : e.Key == VirtualKey.End ? items.Length - 1
            : (current + step + items.Length) % items.Length;
        while (!items[next].IsEnabled) next = (next + step + items.Length) % items.Length;
        items[next].Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private void TrayMenu_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        ((Button)sender).Focus(FocusState.Pointer);

    private void TrayMenu_GotFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button { FocusState: FocusState.Keyboard } button)
            VisualStateManager.GoToState(button, "PointerOver", false);
    }

    private void TrayMenu_LostFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is Button button) VisualStateManager.GoToState(button, "Normal", false);
    }
}
