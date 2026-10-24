using System;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace BrightnessTray.App.Startup;

internal static class StartupSmokeCheck
{
    internal static async Task RunAsync(StartupMenuItem actualItem, DemoStartupRegistration demo)
    {
        await actualItem.RefreshAsync();
        Require(actualItem.Text == "Start with Windows" && actualItem.Enabled && !actualItem.Checked,
            "The startup option did not appear unchecked in the tray menu.");
        actualItem.PerformClick();
        Require(actualItem.Checked && demo.Status.IsEnabled, "The startup menu click did not enable startup.");
        actualItem.PerformClick();
        Require(!actualItem.Checked && !demo.Status.IsEnabled, "The startup menu click did not disable startup.");

        string? error = null;
        using var item = new StartupMenuItem(demo, message => error = message);
        await item.RefreshAsync();
        await item.ToggleAsync();
        demo.FailNextWrite = true;
        await item.ToggleAsync();
        Require(item.Checked && item.Enabled && error is not null,
            "A failed startup change changed the checkmark or disabled subsequent changes.");
        demo.Status = PackagedStartupRegistration.Describe(StartupTaskState.DisabledByUser);
        await item.RefreshAsync();
        Require(!item.Checked && !item.Enabled && item.ToolTipText?.Contains("Settings", StringComparison.Ordinal) == true,
            "The startup menu did not respect a Windows user restriction.");
        demo.Status = PackagedStartupRegistration.Describe(StartupTaskState.EnabledByPolicy);
        await item.RefreshAsync();
        Require(item.Checked && !item.Enabled, "The startup menu did not respect an enabled policy.");
        demo.Status = PackagedStartupRegistration.Describe(StartupTaskState.DisabledByPolicy);
        await item.RefreshAsync();
        Require(!item.Checked && !item.Enabled, "The startup menu did not respect a disabled policy.");
        demo.Status = new(false, true);
        await actualItem.RefreshAsync();
        await WindowsStartupRegistration.Create().GetStatusAsync(); // Read-only check of the installed backend.

        // Test registry operations in an isolated key, never in the real Windows Run key.
        var keyPath = @"Software\BrightnessTray\Tests\Startup-" + Guid.NewGuid().ToString("N");
        const string executable = @"C:\Apps with spaces\BrightnessTray.exe";
        var registration = new RegistryStartupRegistration(executable, keyPath);
        try
        {
            Require(!(await registration.GetStatusAsync()).IsEnabled, "A missing startup entry was reported as enabled.");
            Require((await registration.SetEnabledAsync(true)).IsEnabled, "Startup registration did not persist.");
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
            {
                Require(Equals(key?.GetValue(RegistryStartupRegistration.ValueName), $"\"{executable}\""),
                    "The startup command was not quoted or included unwanted launch arguments.");
                key!.SetValue("Unrelated", "Keep this value");
            }
            var recreated = new RegistryStartupRegistration(executable, keyPath);
            Require((await recreated.GetStatusAsync()).IsEnabled, "Startup state did not survive service recreation.");
            Require(!(await recreated.SetEnabledAsync(false)).IsEnabled, "Disabling startup did not remove the entry.");
            Require(!(await recreated.SetEnabledAsync(false)).IsEnabled, "Disabling startup twice failed.");
            using var retained = Registry.CurrentUser.OpenSubKey(keyPath);
            Require(Equals(retained?.GetValue("Unrelated"), "Keep this value"), "Startup removal changed an unrelated entry.");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
