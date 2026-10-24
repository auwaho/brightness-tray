using System.Runtime.InteropServices;
using BrightnessTray.Windows;

internal static class WindowsBackendTests
{
    internal static Task WmiResultsAsync()
    {
        static object? Read(object output, string name) => ((Dictionary<string, object?>)output)[name];
        static object Result(object? value) => new Dictionary<string, object?> { ["ReturnValue"] = value };
        WmiBrightnessResult.Validate(false, null, (_, _) => throw new Exception("Void method has no result to read."));
        WmiBrightnessResult.Validate(true, Result(0u), Read);
        WmiBrightnessResult.Validate(true, Result((short)0), Read);
        Reject<InvalidOperationException>(() => WmiBrightnessResult.Validate(true, null, Read));
        Reject<InvalidOperationException>(() => WmiBrightnessResult.Validate(true, Result(null), Read));
        Reject<InvalidOperationException>(() => WmiBrightnessResult.Validate(true, Result(DBNull.Value), Read));
        Reject<InvalidOperationException>(() => WmiBrightnessResult.Validate(true, Result(5u), Read));
        Reject<InvalidOperationException>(() => WmiBrightnessResult.Validate(false, Result(5u), Read));
        Reject<COMException>(() => WmiBrightnessResult.Validate(true, Result(0), (_, _) => throw new COMException()));
        return Task.CompletedTask;
    }

    internal static Task EndpointMappingAsync()
    {
        var external = new DisplayEndpoint("Generic PnP Monitor", "external");
        var panel = new DisplayEndpoint("Generic PnP Monitor", "panel");
        var cloned = DisplayEndpointMatcher.Match(["Generic PnP Monitor", "Generic PnP Monitor"], [external, panel]);
        Equal(external, cloned[0]);
        Equal(panel, cloned[1]);
        var reversed = DisplayEndpointMatcher.Match(["Generic PnP Monitor", "Generic PnP Monitor"], [panel, external]);
        Equal(panel, reversed[0]);
        Equal(external, reversed[1]);

        var dell = new DisplayEndpoint("Dell", "dell");
        var boe = new DisplayEndpoint("BOE", "boe");
        var named = DisplayEndpointMatcher.Match([" BOE ", "DELL"], [dell, boe]);
        Equal(boe, named[0]);
        Equal(dell, named[1]);
        var incomplete = DisplayEndpointMatcher.Match(["Generic PnP Monitor", "Generic PnP Monitor"], [external]);
        Equal<DisplayEndpoint?>(null, incomplete[0]);
        Equal<DisplayEndpoint?>(null, incomplete[1]);
        var unique = DisplayEndpointMatcher.Match(["BOE", "Generic PnP Monitor", "Other"], [dell, boe]);
        Equal(boe, unique[0]);
        Equal<DisplayEndpoint?>(null, unique[1]);
        var single = DisplayEndpointMatcher.Match(["Generic PnP Monitor"], [external]);
        Equal(external, single[0]);
        var conflicting = DisplayEndpointMatcher.Match(["BOE", "Unknown"], [dell, boe]);
        Equal(boe, conflicting[0]);
        Equal<DisplayEndpoint?>(null, conflicting[1]);
        return Task.CompletedTask;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
