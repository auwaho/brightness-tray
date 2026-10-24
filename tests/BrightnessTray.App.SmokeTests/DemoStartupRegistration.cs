using System.Threading.Tasks;

namespace BrightnessTray.App.Startup;

// Demo/smoke runs must never register themselves in Windows startup.
internal sealed class DemoStartupRegistration : IStartupRegistration
{
    internal StartupStatus Status { get; set; } = new(false, true, "Demo: startup changes are not saved to Windows.");
    internal bool FailNextWrite { get; set; }

    public Task<StartupStatus> GetStatusAsync() => Task.FromResult(Status);

    public Task<StartupStatus> SetEnabledAsync(bool enabled)
    {
        if (FailNextWrite)
        {
            FailNextWrite = false;
            throw new System.IO.IOException("Simulated startup registration failure.");
        }
        if (Status.CanChange) Status = Status with { IsEnabled = enabled };
        return GetStatusAsync();
    }
}
