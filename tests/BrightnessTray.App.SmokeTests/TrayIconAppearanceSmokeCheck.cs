using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BrightnessTray.App;

internal static class TrayIconAppearanceSmokeCheck
{
    internal static Task RunAsync(DispatcherQueue dispatcher, string directory)
    {
        var dark = Color.FromArgb(26, 26, 26);
        using (var whiteIcon = TrayIconImage.Create(Color.White))
        using (var darkIcon = TrayIconImage.Create(dark))
        using (var white = whiteIcon.ToBitmap())
        using (var black = darkIcon.ToBitmap())
        {
            Require(white.Size == black.Size && white.Width == 32, "The tray icon size changed with its color.");
            for (var y = 0; y < white.Height; y++)
            for (var x = 0; x < white.Width; x++)
                Require(Math.Abs(white.GetPixel(x, y).A - black.GetPixel(x, y).A) <= 1,
                    "Changing the tray color changed the sun geometry or alpha coverage.");
            Require(white.GetPixel(16, 16).A == 0 && black.GetPixel(16, 16).A == 0,
                "The tray sun lost its hollow center.");
            AssertColor(whiteIcon, Color.White);
            AssertColor(darkIcon, dark);
            white.Save(Path.Combine(directory, "tray-glyph-white.png"), ImageFormat.Png);
            black.Save(Path.Combine(directory, "tray-glyph-dark.png"), ImageFormat.Png);
        }

        // Windows mode controls the taskbar independently of the application theme.
        if (!Forms.SystemInformation.HighContrast)
        {
            var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0) is int value && value != 0;
            AssertEqual((light ? dark : Color.White).ToArgb(), TrayIconImage.GetSystemForeground().ToArgb(),
                "The live taskbar color does not follow Windows mode.");
        }
        else
            AssertEqual(SystemColors.WindowText.ToArgb(), TrayIconImage.GetSystemForeground().ToArgb(),
                "High contrast did not use the Windows foreground color.");

        using var tray = new NativeTrayIcon(dispatcher) { Text = "Brightness Tray theme test", Visible = true };
        var foreground = dark;
        var reads = 0;
        using var appearance = new TrayIconAppearance(tray, () => { reads++; return foreground; });
        tray.RequireRegisteredForTest();
        AssertColor(tray.Icon!, dark);
        var initial = tray.Icon;
        foreach (var message in new[] { 0x001A, 0x031A, 0x0015 })
        {
            tray.SendAppearanceMessageForTest(message);
            Require(ReferenceEquals(initial, tray.Icon), "Unchanged settings recreated the tray HICON.");
        }

        foreground = Color.White;
        tray.SendAppearanceMessageForTest(0x001A);
        Require(!ReferenceEquals(initial, tray.Icon), "WM_SETTINGCHANGE did not replace the dark glyph.");
        RequireDisposed(initial!);
        AssertColor(tray.Icon!, Color.White);
        tray.RequireRegisteredForTest();

        var previous = tray.Icon;
        foreground = dark;
        tray.SendAppearanceMessageForTest(0x031A);
        Require(!ReferenceEquals(previous, tray.Icon), "WM_THEMECHANGED did not restore the dark glyph.");
        RequireDisposed(previous!);
        AssertColor(tray.Icon!, dark);

        foreground = Color.Gold;
        tray.SendAppearanceMessageForTest(0x0015);
        AssertColor(tray.Icon!, Color.Gold);

        foreground = Color.White;
        tray.RestartOwnRegistrationForTest();
        tray.RequireRegisteredForTest();
        AssertColor(tray.Icon!, Color.White);
        var afterRestart = tray.Icon;
        tray.SendAppearanceMessageForTest(0x001A);
        Require(ReferenceEquals(afterRestart, tray.Icon), "Explorer recreation caused redundant icon churn.");

        var last = tray.Icon;
        appearance.Dispose();
        Require(tray.Icon is null, "The appearance owner retained its icon after disposal.");
        RequireDisposed(last!);
        var readsAfterDispose = reads;
        foreground = dark;
        tray.SendAppearanceMessageForTest(0x001A);
        AssertEqual(readsAfterDispose, reads, "Disposed appearance still handled theme changes.");
        tray.Dispose();
        tray.DispatchAppearanceMessageForTest(0x031A);
        AssertEqual(readsAfterDispose, reads, "A destroyed tray handled a late theme notification.");
        tray.RequireDisposedForTest();

        var result = JsonSerializer.Serialize(new
        {
            success = true,
            taskbarColorSourceVerified = true,
            lightDarkLightThroughNativeMessages = true,
            unchangedColorReusesIcon = true,
            customForegroundSupported = true,
            explorerRecreationRefreshesColor = true,
            disposedSubscriptionsInactive = true,
            geometryAndAlphaPreserved = true,
            hardwareBrightnessChanged = false,
            windowsSettingsChanged = false,
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "tray-icon-theme.json"), result);
        return Task.CompletedTask;
    }

    private static void AssertColor(Icon icon, Color expected)
    {
        using var bitmap = icon.ToBitmap();
        var opaque = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.A != 255) continue;
            Require(pixel.R == expected.R && pixel.G == expected.G && pixel.B == expected.B,
                "The visible tray glyph has the wrong foreground color.");
            opaque++;
        }
        Require(opaque > 30, "The tray glyph has too few visible pixels.");
    }

    private static void AssertEqual(int expected, int actual, string message) => Require(expected == actual, message);
    private static void RequireDisposed(Icon icon)
    {
        try { _ = icon.Handle; }
        catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("A replaced tray icon retained its native handle.");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed partial class NativeTrayIcon
{
    internal void SendAppearanceMessageForTest(int message) =>
        SendMessageForTest(_window.Handle, message, IntPtr.Zero, IntPtr.Zero);

    internal void DispatchAppearanceMessageForTest(int message)
    {
        var late = Forms.Message.Create(_window.Handle, message, IntPtr.Zero, IntPtr.Zero);
        HandleMessage(ref late);
    }
}
