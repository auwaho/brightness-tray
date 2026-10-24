using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using BrightnessTray.App.ViewModels;
using BrightnessTray.Core;
using BrightnessTray.Core.Diagnostics;
using BrightnessTray.Windows;

internal static class RetryAndDiagnosticsTests
{
    internal static Task ClassificationAsync()
    {
        foreach (var code in new[] { 121, 170, 1237, 1460, unchecked((int)0xC0262582), unchecked((int)0xC0262583), unchecked((int)0xC026258B) })
            Equal(true, RetryingBrightnessDevice.IsTransient(new Win32Exception(code)));
        foreach (var code in new[] { 0x80010109u, 0x8001010Au, 0x80070079u, 0x800700AAu, 0x800704D5u, 0x800705B4u })
            Equal(true, RetryingBrightnessDevice.IsTransient(new COMException("Busy", unchecked((int)code))));
        foreach (var code in new[] { 0, 1, 5, 6, 21, 31, 50, 87, 1167, unchecked((int)0xC0262580), unchecked((int)0xC0262581), unchecked((int)0xC0262584) })
            Equal(false, RetryingBrightnessDevice.IsTransient(new Win32Exception(code)));
        foreach (var error in new Exception[]
        {
            new InvalidOperationException("Display is off"), new ArgumentOutOfRangeException("percent"),
            new ObjectDisposedException("monitor"), new IOException("Unknown failure"),
            new COMException("Unsupported provider", unchecked((int)0x80041010)),
            new COMException("Access denied", unchecked((int)0x80070005))
        }) Equal(false, RetryingBrightnessDevice.IsTransient(error));
        Equal(true, RetryingBrightnessDevice.IsTransient(new TargetInvocationException(
            new InvalidOperationException("Schema", new COMException("Busy", unchecked((int)0x8001010A))))));
        return Task.CompletedTask;
    }

    internal static Task RecoveryAsync()
    {
        using var files = new LogFiles();
        var log = new DiagnosticLog(files.Path);
        var tracker = new NativeOperationTracker();
        var writes = new List<int>();
        var pauses = new List<TimeSpan>();
        var reads = 0;
        var native = new FakeDevice("test", tracker)
        {
            Read = () => ++reads == 1 ? throw new COMException("Busy", unchecked((int)0x8001010A)) : 40,
            Set = value =>
            {
                writes.Add(value);
                if (writes.Count < 3) throw new Win32Exception(121);
                return value;
            }
        };
        using var device = new RetryingBrightnessDevice(native, log, pauses.Add);
        Equal(40, device.ReadBrightness());
        Equal(2, reads);
        Equal(65, device.SetBrightness(65));
        Equal("65,65,65", string.Join(',', writes));
        Equal("60,60,120", string.Join(',', pauses.Select(pause => pause.TotalMilliseconds)));
        var lines = File.ReadAllLines(files.Path);
        Equal(5, lines.Length);
        Equal(true, lines.Any(line => line.Contains("Recovered on attempt 3/3")));
        Equal(true, lines.Any(line => line.Contains("native=0x00000079")));
        // A normal successful call produces no additional log entries or pauses.
        Equal(70, device.SetBrightness(70));
        Equal(5, File.ReadAllLines(files.Path).Length);
        Equal(3, pauses.Count);
        return Task.CompletedTask;
    }

    internal static Task BudgetAndPermanentErrorsAsync()
    {
        var tracker = new NativeOperationTracker();
        foreach (var error in new Exception[] { new Win32Exception(121), new Win32Exception(50), new InvalidOperationException("Display is off") })
        {
            var pauses = 0;
            var native = new FakeDevice("test", tracker) { Read = () => throw error, Set = _ => throw error };
            using var device = new RetryingBrightnessDevice(native, delay: _ => pauses++);
            SameFailure(error, () => device.ReadBrightness());
            SameFailure(error, () => device.SetBrightness(60));
            var transient = error is Win32Exception { NativeErrorCode: 121 };
            Equal(transient ? 3 : 1, native.ReadCount);
            Equal(transient ? 3 : 1, native.SetCount);
            Equal(transient ? 4 : 0, pauses);
        }
        return Task.CompletedTask;
    }

    internal static Task PendingInputAndLifetimeAsync() => UiThreadContext.RunAsync(async () =>
    {
        foreach (var refresh in new[] { false, true })
        {
            var tracker = new NativeOperationTracker();
            using var hold = new BlockingNativeOperation();
            var writes = new ConcurrentQueue<int>();
            var current = 50;
            var first = true;
            var native = new FakeDevice("test", tracker)
            {
                Set = value =>
                {
                    writes.Enqueue(value);
                    if (first) { first = false; throw new Win32Exception(121); }
                    return current = value;
                }
            };
            var initial = true;
            await using var service = new BrightnessService(new FakeDiscovery(tracker, () =>
            {
                if (!initial) return new([new FakeDevice("test", tracker) { Read = () => current }], []);
                initial = false;
                return new([new RetryingBrightnessDevice(native, delay: _ => hold.Execute())], []);
            }));
            await using var flyout = new FlyoutViewModel(service);
            await flyout.RefreshAsync();
            var row = flyout.Displays.Single();
            row.Brightness = 60;
            try
            {
                await hold.Entered;
                Equal(true, row.IsAvailable);
                row.Brightness = 80;
                var finish = refresh ? flyout.RefreshAsync() : flyout.DisposeAsync().AsTask();
                Equal(false, finish.IsCompleted);
                Equal(0, native.DisposeCount);
                hold.Release();
                await finish;
                Equal("60,60,80", string.Join(',', writes));
                Equal(80, current);
                if (refresh)
                {
                    Equal(1, native.DisposeCount);
                    Equal(80d, flyout.Displays.Single().Brightness);
                }
                else
                {
                    await service.DisposeAsync();
                    Equal(1, native.DisposeCount);
                }
            }
            finally { hold.Release(); }
        }
    });

    internal static async Task FinalFailuresAsync()
    {
        using var files = new LogFiles();
        var log = new DiagnosticLog(files.Path);
        var tracker = new NativeOperationTracker();
        var read = new FakeDevice("read", tracker) { Read = () => throw new Win32Exception(121) };
        var write = new FakeDevice("write", tracker) { Set = _ => throw new Win32Exception(121) };
        await using (var service = new BrightnessService(new FakeDiscovery(tracker, () => new(
            [new RetryingBrightnessDevice(read, log, _ => { }), new RetryingBrightnessDevice(write, log, _ => { })],
            [new("Discovery", "Enumeration issue")])), log))
        {
            var result = await service.RefreshAsync();
            Equal<int?>(null, result.Displays[0].BrightnessPercent);
            Equal<int?>(50, result.Displays[1].BrightnessPercent);
            try { await service.SetBrightnessAsync("write", 75); throw new Exception("Write unexpectedly succeeded."); }
            catch (Win32Exception) { }
        }
        var lines = File.ReadAllLines(files.Path);
        Equal(4, lines.Count(line => line.Contains("Retry ")));
        Equal(1, lines.Count(line => line.Contains("Read brightness failed")));
        Equal(1, lines.Count(line => line.Contains("Set brightness failed")));
        Equal(true, lines.Any(line => line.Contains("Requested 75%")));
        Equal(true, lines.Any(line => line.Contains("Enumeration issue")));
        Equal(false, lines.Any(line => line.Contains("Recovered")));
        await using var broken = new BrightnessService(new FakeDiscovery(tracker, () => throw new IOException("Scan failed")), log);
        try { await broken.RefreshAsync(); throw new Exception("Refresh unexpectedly succeeded."); }
        catch (IOException) { }
        Equal(true, File.ReadAllText(files.Path).Contains("Refresh displays failed"));
    }

    internal static Task RotationAndStorageFailureAsync()
    {
        using var files = new LogFiles();
        var log = new DiagnosticLog(files.Path);
        Parallel.For(0, 450, index => log.Write("Diagnostic", "Monitor\r\nInjected", new Win32Exception(121),
            new string('я', 3000) + index));
        Equal(true, File.Exists(files.Path + ".1"));
        Equal(2, Directory.GetFiles(files.Directory).Length);
        foreach (var file in Directory.GetFiles(files.Directory))
        {
            Equal(true, new FileInfo(file).Length <= DiagnosticLog.MaximumFileBytes);
            Equal(true, File.ReadAllLines(file).All(line => line.Contains("Monitor  Injected")));
        }
        log.Write("Last", exception: new COMException("line1\nline2", unchecked((int)0x8001010A)));
        var last = File.ReadAllLines(files.Path)[^1];
        Equal(true, last.Contains("HRESULT=0x8001010A: line1 line2"));
        // A parent path that is a file, and an exclusively locked log, must not escape Write.
        var unavailable = new DiagnosticLog(System.IO.Path.Combine(files.Path, "cannot-write.log"));
        unavailable.Write("No directory");
        using (new FileStream(files.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            log.Write("Locked");
        Equal(last, File.ReadAllLines(files.Path)[^1]);
        return Task.CompletedTask;
    }

    private static void SameFailure(Exception expected, Action invoke)
    {
        try { invoke(); }
        catch (Exception actual) when (ReferenceEquals(expected, actual)) { return; }
        throw new InvalidOperationException("Expected the original driver error.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private sealed class LogFiles : IDisposable
    {
        // The sandbox's redirected Temp permits writes but denies renames; use the workspace for rotation tests.
        internal string Directory { get; } = System.IO.Path.Combine(Environment.CurrentDirectory,
            "artifacts", "validation", "diagnostics-tests", Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "brightness.log");
        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
