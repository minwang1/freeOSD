using System;
using System.Diagnostics;
using System.IO;

namespace MonitorControl.App.Diagnostics;

internal static class DiagnosticLog
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object Sync = new();
    private static TextWriterTraceListener? _listener;

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MonitorControl",
        "Logs",
        "monitor-control.log");

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_listener is not null) return;

            try
            {
                var directory = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(directory);
                var logFile = new FileInfo(LogPath);
                if (logFile.Exists && logFile.Length >= MaxLogBytes)
                {
                    var previousPath = LogPath + ".1";
                    File.Move(LogPath, previousPath, overwrite: true);
                }

                _listener = new TextWriterTraceListener(new FileStream(
                    LogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite));
                Trace.Listeners.Add(_listener);
                Trace.AutoFlush = true;
                Write("INFO", "app.started", string.Empty);
            }
            catch (Exception exception)
            {
                // Logging must never prevent monitor controls from starting.
                Trace.WriteLine($"diagnostics.initialize.failed type={exception.GetType().Name}");
            }
        }
    }

    public static void Write(string level, string eventName, string details)
    {
        try
        {
            Initialize();
            var safeDetails = details.Replace('\r', ' ').Replace('\n', ' ');
            var line = $"{DateTimeOffset.UtcNow:O} level={level} event={eventName} {safeDetails}".TrimEnd();
            lock (Sync)
            {
                // The listener already holds the log file open. Reopening that file with
                // File.AppendAllText can fail Windows' bidirectional share-mode checks;
                // the old broad catch then silently discarded every structured event.
                _listener?.WriteLine(line);
                _listener?.Flush();
            }
        }
        catch
        {
            // Diagnostics are best-effort and must not interrupt device operations.
        }
    }
}
