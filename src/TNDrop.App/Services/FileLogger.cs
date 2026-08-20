using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace TNDrop.Services;

public sealed class FileLogger
{
    private readonly string _logDir;
    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();
    private static readonly Encoding _utf8NoBom = new UTF8Encoding(false);

    public FileLogger(string logDir, Func<DateTime>? clock = null)
    {
        _logDir = logDir;
        _clock = clock ?? (() => DateTime.Now);

        try
        {
            Directory.CreateDirectory(_logDir);
        }
        catch
        {
            // Suppress errors creating directory
        }
    }

    public void Info(string module, string message)
    {
        Log("INFO", module, message, null);
    }

    public void Warn(string module, string message)
    {
        Log("WARN", module, message, null);
    }

    public void Error(string module, string message, Exception? ex = null)
    {
        Log("ERROR", module, message, ex);
    }

    private void Log(string level, string module, string message, Exception? ex)
    {
        lock (_lock)
        {
            try
            {
                var now = _clock();
                var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                var time = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                var filename = Path.Combine(_logDir, $"app-{date}.log");

                var sb = new StringBuilder();
                sb.Append($"{time} [{level}] {module}: {message}");

                if (ex != null)
                {
                    sb.AppendLine();
                    sb.Append($"  {ex.GetType().Name}: {ex.Message}");

                    if (!string.IsNullOrEmpty(ex.StackTrace))
                    {
                        sb.AppendLine();
                        var stackLines = ex.StackTrace.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
                        foreach (var line in stackLines)
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                sb.Append($"    {line}");
                                sb.AppendLine();
                            }
                        }
                    }
                }

                File.AppendAllText(filename, sb.ToString() + Environment.NewLine, _utf8NoBom);
            }
            catch
            {
                // Suppress write errors - don't crash the app
            }
        }
    }

    public static FileLogger? Instance { get; set; }
}
