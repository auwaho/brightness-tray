using System.ComponentModel;
using System.Text;

namespace BrightnessTray.Core.Diagnostics;

// Best-effort local diagnostics. No persistent stream, background worker or external telemetry.
public sealed class DiagnosticLog(string path)
{
    public const int MaximumFileBytes = 256 * 1024;
    private readonly object _gate = new();

    public void Write(string operation, string? device = null, Exception? exception = null, string? detail = null)
    {
        try
        {
            var entry = new StringBuilder($"{DateTimeOffset.UtcNow:O} {operation}");
            if (device is not null) entry.Append($" | device={device}");
            if (detail is not null) entry.Append($" | {detail}");
            for (var current = exception; current is not null && entry.Length < 2048; current = current.InnerException)
            {
                entry.Append($" | {current.GetType().Name} HRESULT=0x{current.HResult:X8}");
                if (current is Win32Exception native) entry.Append($" native=0x{native.NativeErrorCode:X8}");
                entry.Append($": {current.Message}");
            }
            var line = entry.ToString();
            if (line.Length > 2048) line = line[..2048];
            line = line.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ') + Environment.NewLine;
            var bytes = Encoding.UTF8.GetBytes(line);
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > MaximumFileBytes)
                    File.Move(path, path + ".1", overwrite: true);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            }
        }
        catch (Exception)
        {
            // Logging must never turn a recoverable driver error or a full disk into an app failure.
        }
    }
}
