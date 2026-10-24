using System.Threading.Tasks;

namespace BrightnessTray.App.Startup;

internal sealed record StartupStatus(bool IsEnabled, bool CanChange, string? Reason = null);

internal interface IStartupRegistration
{
    Task<StartupStatus> GetStatusAsync();
    Task<StartupStatus> SetEnabledAsync(bool enabled);
}
