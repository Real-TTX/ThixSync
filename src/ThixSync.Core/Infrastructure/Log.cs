using System.Text;

namespace ThixSync.Core.Infrastructure;

/// <summary>
/// A small daily log file in the data folder. Never write passwords or tokens into it: the log is
/// what a user sends along with a problem.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO ", message);

    public static void Warning(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                var path = Path.Combine(AppPaths.LogDirectory, $"thixsync-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // logging must never break the application
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
