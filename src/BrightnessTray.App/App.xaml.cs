using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BrightnessTray.App.ViewModels;
using BrightnessTray.App.Startup;
using BrightnessTray.Core;
using BrightnessTray.Core.Diagnostics;
using BrightnessTray.Windows;
using Microsoft.UI.Xaml;
using Forms = System.Windows.Forms;

namespace BrightnessTray.App;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsMutex;
    private NativeTrayIcon? _tray;
    private TrayBrightnessController? _trayBrightness;
    private TrayBrightnessToolTip? _trayToolTip;
    private Forms.ContextMenuStrip? _menu;
    private Icon? _icon;
    private FlyoutWindow? _flyout;
    private BrightnessService? _service;
    private bool _exiting;
    private StartupMenuItem? _startupItem;
    private readonly DiagnosticLog _diagnostics = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BrightnessTray", "Logs", "brightness.log"));

    public App()
    {
        UnhandledException += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            ReportFailure(eventArgs.Exception);
            _ = ExitAsync();
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var userKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..16];
        _instance = new Mutex(false, $"Local\\BrightnessTray.{userKey}");
        try { _ownsMutex = _instance.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        if (!_ownsMutex)
        {
            Forms.MessageBox.Show("BrightnessTray is already running. Click the sun icon in the system tray.", "BrightnessTray");
            _instance.Dispose();
            Exit();
            return;
        }

        _diagnostics.Write("Application started", detail: $"Version {typeof(App).Assembly.GetName().Version}");
        _service = new BrightnessService(new WindowsDisplayDiscovery(_diagnostics), _diagnostics);
        _flyout = new FlyoutWindow(new FlyoutViewModel(_service));
        _icon = TrayIconImage.Create();
        Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("Open brightness", null, (_, _) => _flyout.ShowNearTray());
        _menu.Items.Add("Refresh displays", null, async (_, _) =>
        {
            await _flyout.RefreshOrShowAsync();
        });
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _startupItem = new StartupMenuItem(WindowsStartupRegistration.Create(),
            message => Forms.MessageBox.Show(message, "BrightnessTray"));
        _menu.Items.Add(_startupItem);
        _menu.Opening += async (_, _) => await _startupItem.RefreshAsync();
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        _trayBrightness = new TrayBrightnessController(_flyout.ViewModel, () => !_flyout.IsVisible);
        _tray = new NativeTrayIcon(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread())
        {
            Icon = _icon,
            Text = "BrightnessTray · Display brightness",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _trayToolTip = new TrayBrightnessToolTip(_flyout.ViewModel, text =>
        {
            if (_tray is not null) _tray.Text = text;
        }, () => !_flyout.IsVisible);
        _tray.MouseHover += async () =>
        {
            try { await _trayToolTip.RefreshOnHoverAsync(); }
            catch (Exception exception) { ReportFailure(exception); }
        };
        _tray.MouseWheel += async delta =>
        {
            try { await _trayBrightness.ScrollAsync(delta); }
            catch (Exception exception) { ReportFailure(exception); }
        };
        _tray.MouseDown += async (_, eventArgs) =>
        {
            if (eventArgs.Button != Forms.MouseButtons.Left) return;
            await _flyout.ToggleFromTrayAsync();
        };
        if (arguments.Contains("--show", StringComparer.OrdinalIgnoreCase)) _flyout.ShowNearTray();
    }

    private void ReportFailure(Exception exception)
    {
        _diagnostics.Write("Application operation failed", exception: exception);
        Environment.ExitCode = 1;
        Forms.MessageBox.Show($"The operation failed: {exception.Message}", "BrightnessTray");
    }

    internal async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        if (_tray is not null) _tray.Visible = false;
        try
        {
            if (_trayToolTip is not null) await _trayToolTip.DisposeAsync();
            if (_trayBrightness is not null) await _trayBrightness.DisposeAsync();
            if (_flyout is not null)
            {
                _flyout.Hide();
                await _flyout.ViewModel.DisposeAsync();
            }
            if (_service is not null) await _service.DisposeAsync();
        }
        catch (Exception exception) { ReportFailure(exception); }
        finally
        {
            _tray?.Dispose();
            _menu?.Dispose();
            _icon?.Dispose();
            if (_ownsMutex) { _instance?.ReleaseMutex(); _ownsMutex = false; }
            _instance?.Dispose();
            try
            {
                _flyout?.CloseForExit();
            }
            catch (Exception exception) { ReportFailure(exception); }
            finally
            {
                _diagnostics.Write("Application stopped");
                Exit();
            }
        }
    }
}
