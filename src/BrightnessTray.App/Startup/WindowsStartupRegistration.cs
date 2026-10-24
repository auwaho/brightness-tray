using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace BrightnessTray.App.Startup;

internal sealed class WindowsStartupRegistration : IStartupRegistration
{
    internal const string TaskId = "BrightnessTrayStartup";
    private IStartupRegistration? _backend;

    internal static IStartupRegistration Create() => new WindowsStartupRegistration();

    public Task<StartupStatus> GetStatusAsync() => Backend.GetStatusAsync();
    public Task<StartupStatus> SetEnabledAsync(bool enabled) => Backend.SetEnabledAsync(enabled);
    private IStartupRegistration Backend => _backend ??= CreateBackend();

    private static IStartupRegistration CreateBackend()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, IntPtr.Zero);
        return result switch
        {
            122 => new PackagedStartupRegistration(), // ERROR_INSUFFICIENT_BUFFER: package identity exists.
            15700 => new RegistryStartupRegistration(Environment.ProcessPath
                ?? throw new InvalidOperationException("The application path is unavailable.")),
            _ => throw new Win32Exception(result),
        };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}

internal sealed class RegistryStartupRegistration : IStartupRegistration
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "BrightnessTray";
    private readonly string _keyPath;
    private readonly string _command;

    internal RegistryStartupRegistration(string executablePath, string keyPath = RunKeyPath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Contains('"'))
            throw new ArgumentException("A valid application path is required.", nameof(executablePath));
        _command = $"\"{executablePath}\"";
        if (_command.Length > 260)
            throw new ArgumentException("The application path is too long for Windows startup registration.", nameof(executablePath));
        _keyPath = keyPath;
    }

    public Task<StartupStatus> GetStatusAsync()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return Task.FromResult(new StartupStatus(key?.GetValue(ValueName) is string command
            && string.Equals(command, _command, StringComparison.OrdinalIgnoreCase), true));
    }

    public Task<StartupStatus> SetEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
            key.SetValue(ValueName, _command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        return GetStatusAsync();
    }
}

internal sealed class PackagedStartupRegistration : IStartupRegistration
{
    public async Task<StartupStatus> GetStatusAsync() => Describe((await GetTaskAsync()).State);

    public async Task<StartupStatus> SetEnabledAsync(bool enabled)
    {
        var task = await GetTaskAsync();
        if (!Describe(task.State).CanChange) return Describe(task.State);
        if (enabled) return Describe(await task.RequestEnableAsync());
        task.Disable();
        return Describe(task.State);
    }

    private static async Task<StartupTask> GetTaskAsync() => await StartupTask.GetAsync(WindowsStartupRegistration.TaskId)
        ?? throw new InvalidOperationException("The installed package is missing its startup task.");

    internal static StartupStatus Describe(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => new(true, true),
        StartupTaskState.Disabled => new(false, true),
        StartupTaskState.DisabledByUser => new(false, false,
            "Enable BrightnessTray in Windows Settings > Apps > Startup."),
        StartupTaskState.EnabledByPolicy => new(true, false, "Startup is managed by your organization."),
        _ => new(false, false, "Startup is unavailable or disabled by your organization."),
    };
}
