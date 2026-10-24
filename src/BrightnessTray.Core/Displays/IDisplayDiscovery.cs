namespace BrightnessTray.Core.Displays;

public interface IDisplayDiscovery
{
    // Success transfers fresh device instances to the caller; failure must release partial discoveries.
    DisplayDiscoveryResult Discover();
}

public sealed record DisplayIssue(string Name, string Message);

public sealed record DisplayDiscoveryResult(
    IReadOnlyList<IBrightnessDevice> Devices,
    IReadOnlyList<DisplayIssue> Issues);

public sealed record DisplaySnapshot(
    string Id,
    string Name,
    string ConnectionLabel,
    int? BrightnessPercent,
    string? Error);

public sealed record DisplayRefreshResult(
    IReadOnlyList<DisplaySnapshot> Displays,
    IReadOnlyList<DisplayIssue> Issues);
