namespace BrightnessTray.Core.Displays;

// Drivers are synchronous; BrightnessService serializes them off the UI thread.
public interface IBrightnessDevice : IDisposable
{
    string Id { get; }
    string Name { get; }
    string ConnectionLabel { get; }
    int ReadBrightness();
    // Returns the accepted percentage after device-specific rounding; it need not equal the request.
    int SetBrightness(int percent);
}
