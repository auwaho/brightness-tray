using System;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace BrightnessTray.App.Startup;

internal sealed class StartupMenuItem : Forms.ToolStripMenuItem
{
    private readonly IStartupRegistration _registration;
    private readonly Action<string> _showError;
    private StartupStatus _status = new(false, false);
    internal bool IsBusy { get; private set; }

    internal StartupMenuItem(IStartupRegistration registration, Action<string> showError) : base("Start with Windows")
    {
        _registration = registration;
        _showError = showError;
        Enabled = false;
        CheckOnClick = false;
        Click += async (_, _) => await ToggleAsync();
    }

    internal async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Enabled = false;
        try { Apply(await _registration.GetStatusAsync()); }
        catch (Exception exception) { Apply(new(false, false, $"Startup setting unavailable: {exception.Message}")); }
        finally { IsBusy = false; }
    }

    internal async Task ToggleAsync()
    {
        if (IsBusy || !Enabled) return;
        IsBusy = true;
        Enabled = false;
        try { Apply(await _registration.SetEnabledAsync(!_status.IsEnabled)); }
        catch (Exception exception)
        {
            Apply(_status);
            _showError($"Could not change startup setting: {exception.Message}");
        }
        finally { IsBusy = false; }
    }

    private void Apply(StartupStatus status)
    {
        _status = status;
        Checked = status.IsEnabled;
        Enabled = status.CanChange;
        ToolTipText = status.Reason ?? "Start BrightnessTray in the system tray when you sign in.";
    }
}
