using System.Collections.Concurrent;
using System.Text;

namespace SystemChangeMonitor.Logging;

/// <summary>
/// Centralized log manager that handles all logging operations with thread-safe file writing.
/// </summary>
public sealed class LogManager : IDisposable
{
    private readonly string _logDirectory;
    private readonly ConcurrentDictionary<string, StreamWriter> _logWriters;
    private readonly ConcurrentQueue<LogEntry> _logQueue;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Task _writerTask;
    private readonly object _lockObject = new();
    private bool _disposed;

    public LogManager(string? logDirectory = null)
    {
        // Requirement: log files must be stored in a Logs folder within the application's directory.
        // AppDomain.CurrentDomain.BaseDirectory points to the folder containing the running executable.
        _logDirectory = logDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        _logWriters = new ConcurrentDictionary<string, StreamWriter>();
        _logQueue = new ConcurrentQueue<LogEntry>();
        _cancellationTokenSource = new CancellationTokenSource();

        // Ensure log directory exists
        Directory.CreateDirectory(_logDirectory);

        // Start background writer task
        _writerTask = Task.Run(ProcessLogQueueAsync);

        Log("System", "LogManager", "Log manager initialized", LogLevel.Info);
    }

    public string LogDirectory => _logDirectory;

    /// <summary>
    /// Logs an event to the appropriate log file based on category.
    /// </summary>
    public void Log(string category, string source, string message, LogLevel level = LogLevel.Info, Dictionary<string, string>? additionalData = null)
    {
        if (_disposed) return;

        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Category = category,
            Source = source,
            Message = message,
            Level = level,
            AdditionalData = additionalData
        };

        _logQueue.Enqueue(entry);
    }

    /// <summary>
    /// Logs a file system event with detailed information.
    /// </summary>
    public void LogFileSystemEvent(string eventType, string path, string? oldPath = null, string? details = null)
    {
        var data = new Dictionary<string, string>
        {
            ["EventType"] = eventType,
            ["Path"] = path
        };

        if (!string.IsNullOrEmpty(oldPath))
            data["OldPath"] = oldPath;

        if (!string.IsNullOrEmpty(details))
            data["Details"] = details;

        // Try to get file attributes if file exists
        try
        {
            if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                data["Size"] = fileInfo.Length.ToString();
                data["LastModified"] = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
                data["Attributes"] = fileInfo.Attributes.ToString();
            }
            else if (Directory.Exists(path))
            {
                data["IsDirectory"] = "true";
                var dirInfo = new DirectoryInfo(path);
                data["Attributes"] = dirInfo.Attributes.ToString();
            }
        }
        catch
        {
            // File may have been deleted or access denied
        }

        Log("FileSystem", "FileSystemWatcher", $"{eventType}: {path}", LogLevel.Info, data);
    }

    /// <summary>
    /// Logs a Windows Event Log entry.
    /// </summary>
    public void LogWindowsEvent(string logName, string source, int eventId, string entryType, string message, DateTime timeGenerated)
    {
        var data = new Dictionary<string, string>
        {
            ["LogName"] = logName,
            ["Source"] = source,
            ["EventId"] = eventId.ToString(),
            ["EntryType"] = entryType,
            ["TimeGenerated"] = timeGenerated.ToString("yyyy-MM-dd HH:mm:ss.fff")
        };

        Log("WindowsEvents", source, message, ConvertEntryTypeToLogLevel(entryType), data);
    }

    /// <summary>
    /// Logs a registry change event.
    /// </summary>
    public void LogRegistryEvent(string keyPath, string changeType, string? valueName = null, string? details = null)
    {
        var data = new Dictionary<string, string>
        {
            ["KeyPath"] = keyPath,
            ["ChangeType"] = changeType
        };

        if (!string.IsNullOrEmpty(valueName))
            data["ValueName"] = valueName;

        if (!string.IsNullOrEmpty(details))
            data["Details"] = details;

        Log("Registry", "RegistryWatcher", $"{changeType}: {keyPath}", LogLevel.Info, data);
    }

    /// <summary>
    /// Logs a system setting change.
    /// </summary>
    public void LogSettingChange(string settingCategory, string settingName, string? oldValue, string? newValue, string? details = null)
    {
        var data = new Dictionary<string, string>
        {
            ["SettingCategory"] = settingCategory,
            ["SettingName"] = settingName,
            ["OldValue"] = oldValue ?? "(unknown)",
            ["NewValue"] = newValue ?? "(unknown)"
        };

        if (!string.IsNullOrEmpty(details))
            data["Details"] = details;

        Log("Settings", "SettingsMonitor", $"Setting changed: {settingCategory}/{settingName}", LogLevel.Info, data);
    }

    /// <summary>
    /// Logs a service state change.
    /// </summary>
    public void LogServiceEvent(string serviceName, string status, string? details = null)
    {
        var data = new Dictionary<string, string>
        {
            ["ServiceName"] = serviceName,
            ["Status"] = status
        };

        if (!string.IsNullOrEmpty(details))
            data["Details"] = details;

        Log("Services", "ServiceMonitor", $"Service {serviceName}: {status}", LogLevel.Info, data);
    }

    private async Task ProcessLogQueueAsync()
    {
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                while (_logQueue.TryDequeue(out var entry))
                {
                    await WriteLogEntryAsync(entry);
                }

                await Task.Delay(100, _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LogManager Error] {ex.Message}");
            }
        }

        // Process remaining entries before shutdown
        while (_logQueue.TryDequeue(out var entry))
        {
            try
            {
                await WriteLogEntryAsync(entry);
            }
            catch
            {
                // Ignore errors during shutdown
            }
        }
    }

    private async Task WriteLogEntryAsync(LogEntry entry)
    {
        var writer = GetOrCreateWriter(entry.Category);
        var logLine = FormatLogEntry(entry);

        await writer.WriteLineAsync(logLine);
        await writer.FlushAsync();

        // Also write to console with color coding
        WriteToConsole(entry);
    }

    private StreamWriter GetOrCreateWriter(string category)
    {
        var dateStr = DateTime.Now.ToString("yyyy-MM-dd");
        var fileName = $"{category}_{dateStr}.log";
        var key = $"{category}_{dateStr}";

        return _logWriters.GetOrAdd(key, _ =>
        {
            var filePath = Path.Combine(_logDirectory, fileName);
            // Allow other processes (and tests) to read the log while we keep writing.
            var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };
        });
    }

    private static string FormatLogEntry(LogEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] ");
        sb.Append($"[{entry.Level}] ");
        sb.Append($"[{entry.Source}] ");
        sb.Append(entry.Message);

        if (entry.AdditionalData != null && entry.AdditionalData.Count > 0)
        {
            sb.Append(" | ");
            sb.Append(string.Join(", ", entry.AdditionalData.Select(kvp => $"{kvp.Key}={kvp.Value}")));
        }

        return sb.ToString();
    }

    private static void WriteToConsole(LogEntry entry)
    {
        var originalColor = Console.ForegroundColor;

        Console.ForegroundColor = entry.Level switch
        {
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Info => ConsoleColor.White,
            LogLevel.Debug => ConsoleColor.Gray,
            _ => ConsoleColor.White
        };

        var categoryColor = entry.Category switch
        {
            "FileSystem" => ConsoleColor.Cyan,
            "WindowsEvents" => ConsoleColor.Magenta,
            "Registry" => ConsoleColor.Green,
            "Settings" => ConsoleColor.Blue,
            "Services" => ConsoleColor.DarkYellow,
            _ => ConsoleColor.White
        };

        Console.ForegroundColor = categoryColor;
        Console.Write($"[{entry.Timestamp:HH:mm:ss.fff}] ");

        Console.ForegroundColor = entry.Level switch
        {
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Warning => ConsoleColor.Yellow,
            _ => ConsoleColor.White
        };

        Console.WriteLine($"[{entry.Category}] [{entry.Source}] {entry.Message}");
        Console.ForegroundColor = originalColor;
    }

    private static LogLevel ConvertEntryTypeToLogLevel(string entryType)
    {
        return entryType.ToLowerInvariant() switch
        {
            "error" => LogLevel.Error,
            "warning" => LogLevel.Warning,
            "information" => LogLevel.Info,
            "successaudit" => LogLevel.Info,
            "failureaudit" => LogLevel.Warning,
            _ => LogLevel.Info
        };
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Log shutdown before flipping the disposed flag (Log() is a no-op when disposed).
        Log("System", "LogManager", "Shutting down log manager...", LogLevel.Info);
        _disposed = true;

        _cancellationTokenSource.Cancel();

        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Ignore timeout
        }

        foreach (var writer in _logWriters.Values)
        {
            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }

        _logWriters.Clear();
        _cancellationTokenSource.Dispose();
    }
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

internal class LogEntry
{
    public DateTime Timestamp { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public LogLevel Level { get; set; }
    public Dictionary<string, string>? AdditionalData { get; set; }
}
