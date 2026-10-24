using System;
using System.Threading;
using BrightnessTray.Core.Displays;

namespace BrightnessTray.App;

internal sealed class DemoDisplayDiscovery : IDisplayDiscovery
{
    private readonly DemoBrightness _external = new(65);
    private readonly DemoBrightness _internal = new(80);
    private int _discoveryCount;
    internal int DiscoveryCount => Volatile.Read(ref _discoveryCount);
    internal bool InternalAvailable { get => _internal.Available; set => _internal.Available = value; }
    internal int InternalWriteCount => _internal.WriteCount;
    internal ManualResetEventSlim? DiscoveryGate { get; set; }
    public DisplayDiscoveryResult Discover()
    {
        Interlocked.Increment(ref _discoveryCount);
        DiscoveryGate?.Wait();
        return new(
        [new DemoDevice("demo-external", "DELL U2723QE", "DDC/CI", _external),
         new DemoDevice("demo-internal", "Laptop display", "WMI", _internal)], []);
    }

    private sealed class DemoBrightness(int value)
    {
        public int Value { get; set; } = value;
        public bool Available { get; set; } = true;
        public int WriteCount { get; set; }
    }
    private sealed class DemoDevice(string id, string name, string connectionLabel, DemoBrightness brightness) : IBrightnessDevice
    {
        public string Id => id;
        public string Name => name;
        public string ConnectionLabel => connectionLabel;
        public int ReadBrightness() => brightness.Available ? brightness.Value
            : throw new System.InvalidOperationException("The display is switched off. Turn it on, then refresh the display list.");
        public int SetBrightness(int percent)
        {
            if (!brightness.Available) throw new System.InvalidOperationException("The display is switched off.");
            brightness.WriteCount++;
            return brightness.Value = percent;
        }
        public void Dispose() { }
    }
}
