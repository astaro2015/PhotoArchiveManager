using System.Text;

namespace PhotoArchiveManager.Services;

public static class LoggingService
{
    private static readonly object Sync = new();
    private static string? _logFile;

    public static void Initialize(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);
        _logFile = Path.Combine(logsDirectory, $"PhotoArchiveManager_{DateTime.Now:yyyyMMdd}.log");
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .Append(message);
            if (ex is not null)
                line.AppendLine().Append(ex);

            lock (Sync)
            {
                if (_logFile is not null)
                    File.AppendAllText(_logFile, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never crash the application.
        }
    }
}
