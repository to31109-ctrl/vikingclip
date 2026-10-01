using System.Collections.Concurrent;
using System.Text;

namespace VikingClip.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

public readonly record struct LogEntry(DateTime Time, LogLevel Level, string Message);

/// <summary>Tiny file logger: one file per day in %AppData%\VikingClip\logs, 7 days kept.
/// Also keeps the last 500 entries in memory for the diagnostics page.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static string? _writerDate;
    private static readonly ConcurrentQueue<LogEntry> Recent = new();

    public static event Action<LogEntry>? EntryAdded;

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message, Exception? ex = null) =>
        Write(LogLevel.Error, ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    public static IReadOnlyList<LogEntry> RecentEntries => Recent.ToArray();

    public static string CurrentLogFile => Path.Combine(Paths.LogsDir, $"vikingclip-{DateTime.Now:yyyyMMdd}.log");

    private static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        Recent.Enqueue(entry);
        while (Recent.Count > 500 && Recent.TryDequeue(out _)) { }

        var line = $"{entry.Time:HH:mm:ss.fff} [{level,-5}] {message}";
        System.Diagnostics.Debug.WriteLine(line);

        lock (Gate)
        {
            try
            {
                var date = DateTime.Now.ToString("yyyyMMdd");
                if (_writer is null || _writerDate != date)
                {
                    _writer?.Dispose();
                    _writerDate = date;
                    _writer = new StreamWriter(CurrentLogFile, append: true, Encoding.UTF8) { AutoFlush = true };
                    CleanupOldLogs();
                }
                _writer.WriteLine(line);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }

        try { EntryAdded?.Invoke(entry); } catch { }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Paths.LogsDir, "vikingclip-*.log"))
            {
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f);
            }
        }
        catch { }
    }
}
