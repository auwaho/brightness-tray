# Brightness Tray

A small Windows 11 tray app for controlling each display's hardware brightness.
Built with C# and WinUI 3.

| Light | Dark |
| --- | --- |
| ![Light appearance](assets/screenshots/light.png) | ![Dark appearance](assets/screenshots/dark.png) |

- Separate brightness controls for external (DDC/CI) and built-in (WMI) displays.
- Mouse wheel control over the tray icon or individual sliders.
- Windows light and dark themes, transparency, and an adaptive tray icon.
- Optional startup with Windows.

Enable DDC/CI in your monitor's settings if needed. Some displays, drivers and
adapters do not support hardware brightness control.

## Build and run

Requires Windows 11 x64, .NET 10 SDK and PowerShell 7.

```powershell
pwsh -File .\scripts\publish.ps1
.\artifacts\publish\win-x64\BrightnessTray.exe
```

The publish folder is portable; copy it in full when moving the app.
