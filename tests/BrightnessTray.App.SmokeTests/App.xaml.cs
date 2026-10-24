using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BrightnessTray.App.Startup;
using BrightnessTray.App.ViewModels;
using BrightnessTray.Core;
using Microsoft.UI.Xaml;
using Forms = System.Windows.Forms;

namespace BrightnessTray.App;

public partial class App : Application
{
    private readonly string _outputDirectory;
    private bool _failed;

    public App()
    {
        var arguments = Environment.GetCommandLineArgs();
        _outputDirectory = Path.GetFullPath(arguments.Length > 1 ? arguments[1] : "artifacts/smoke-ui");
        InitializeComponent();
        UnhandledException += (_, args) => { args.Handled = true; ReportFailure(args.Exception); };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Directory.CreateDirectory(_outputDirectory);
        Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
        using var errors = new BackdropCallbackErrorTracker();
        var discovery = new DemoDisplayDiscovery();
        await using var service = new BrightnessService(discovery);
        var viewModel = new FlyoutViewModel(service);
        var window = new FlyoutWindow(viewModel, requireForeground: false);
        window.ObserveMotion();
        using var icon = TrayIconImage.Create();
        using var tray = new NativeTrayIcon(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread())
            { Icon = icon, Text = "BrightnessTray UI test", Visible = true };
        var startup = new DemoStartupRegistration();
        using var menuItem = new StartupMenuItem(startup, message => throw new InvalidOperationException(message));
        try
        {
            await TrayMouseWheelSmokeCheck.RunAsync(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            await StartupSmokeCheck.RunAsync(menuItem, startup);
            if (Environment.GetCommandLineArgs().Contains("--tray-menu"))
                await TrayMenuSmokeCheck.RunAsync(window, menuItem, startup, discovery, errors, _outputDirectory);
            else
            {
                window.ShowNearTray();
                await UiSmokeCheck.RunAsync(window, discovery, errors, _outputDirectory,
                    Environment.GetCommandLineArgs().Contains("--native-capture"));
            }
        }
        catch (Exception exception) { ReportFailure(exception); }
        finally
        {
            tray.Visible = false;
            window.Hide();
            await viewModel.DisposeAsync();
            window.CloseForExit();
            try { errors.ThrowIfAny(); }
            catch (Exception exception) { ReportFailure(exception); }
            Environment.ExitCode = _failed ? 1 : 0;
            Exit();
        }
    }

    private void ReportFailure(Exception exception)
    {
        _failed = true;
        Directory.CreateDirectory(_outputDirectory);
        File.WriteAllText(Path.Combine(_outputDirectory, "result.json"), "{\"success\": false}");
        File.WriteAllText(Path.Combine(_outputDirectory, "failure.txt"), exception.ToString());
    }
}
