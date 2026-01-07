using System.Diagnostics;
using SystemChangeMonitor.Logging;

namespace SystemChangeMonitor.Monitors;

/// <summary>
/// Monitors Windows Event Logs for system changes, security events, and application events.
/// </summary>
public sealed class WindowsEventMonitor : IDisposable
{
    private readonly LogManager _logManager;
    private readonly List<EventLog> _eventLogs;
    private readonly HashSet<string> _processedEvents;
    private readonly object _lockObject = new();
    private bool _disposed;

    // Event logs to monitor for system changes
    private static readonly string[] MonitoredLogs = new[]
    {
        "System",
        "Application", 
        "Security",
        "Microsoft-Windows-GroupPolicy/Operational",
        "Microsoft-Windows-WindowsUpdateClient/Operational",
        "Microsoft-Windows-Bits-Client/Operational",
        "Microsoft-Windows-User Profile Service/Operational"
    };

    public WindowsEventMonitor(LogManager logManager)
    {
        _logManager = logManager;
        _eventLogs = new List<EventLog>();
        _processedEvents = new HashSet<string>();
    }

    public void Start()
    {
        _logManager.Log("WindowsEvents", "WindowsEventMonitor", "Starting Windows Event Log monitoring...", LogLevel.Info);

        foreach (var logName in MonitoredLogs)
        {
            try
            {
                if (EventLog.Exists(logName))
                {
                    var eventLog = new EventLog(logName) { EnableRaisingEvents = true };
                    eventLog.EntryWritten += OnEntryWritten;
                    _eventLogs.Add(eventLog);
                    _logManager.Log("WindowsEvents", "WindowsEventMonitor", $"Monitoring event log: {logName}", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                _logManager.Log("WindowsEvents", "WindowsEventMonitor", $"Failed to monitor {logName}: {ex.Message}", LogLevel.Warning);
            }
        }

        _logManager.Log("WindowsEvents", "WindowsEventMonitor", $"Windows Event monitoring started with {_eventLogs.Count} logs", LogLevel.Info);
    }

    private void OnEntryWritten(object sender, EntryWrittenEventArgs e)
    {
        if (_disposed) return;

        try
        {
            var entry = e.Entry;
            var eventKey = $"{entry.Source}:{entry.InstanceId}:{entry.TimeGenerated.Ticks}";

            lock (_lockObject)
            {
                if (_processedEvents.Contains(eventKey)) return;
                if (_processedEvents.Count > 10000) _processedEvents.Clear();
                _processedEvents.Add(eventKey);
            }

            if (IsRelevantEvent(entry))
            {
                var logName = (sender as EventLog)?.Log ?? "Unknown";
                _logManager.LogWindowsEvent(
                    logName,
                    entry.Source,
                    (int)entry.InstanceId,
                    entry.EntryType.ToString(),
                    TruncateMessage(entry.Message),
                    entry.TimeGenerated
                );
            }
        }
        catch (Exception ex)
        {
            _logManager.Log("WindowsEvents", "WindowsEventMonitor", $"Error processing event: {ex.Message}", LogLevel.Error);
        }
    }

    private static bool IsRelevantEvent(EventLogEntry entry)
    {
        // Filter for system change related events
        var relevantSources = new[]
        {
            "Service Control Manager",
            "Microsoft-Windows-Security-Auditing",
            "Microsoft-Windows-GroupPolicy",
            "Microsoft-Windows-WindowsUpdateClient",
            "Microsoft-Windows-Kernel-General",
            "Microsoft-Windows-Kernel-Power",
            "Microsoft-Windows-User Profiles Service",
            "Microsoft-Windows-DNS-Client",
            "Microsoft-Windows-WLAN-AutoConfig",
            "Microsoft-Windows-NetworkProfile",
            "EventLog",
            "Netlogon",
            "DCOM",
            "Windows Error Reporting",
            "Application Error",
            "Windows Installer",
            "MsiInstaller",
            "Software Protection Platform Service",
            "VSS",
            "Schannel",
            "Microsoft-Windows-Winlogon",
            "Microsoft-Windows-Security-SPP",
            "Microsoft-Windows-RestartManager",
            "Microsoft-Windows-TaskScheduler"
        };

        // Always include errors and warnings
        if (entry.EntryType == EventLogEntryType.Error || entry.EntryType == EventLogEntryType.Warning)
            return true;

        // Include audit events
        if (entry.EntryType == EventLogEntryType.SuccessAudit || entry.EntryType == EventLogEntryType.FailureAudit)
            return true;

        // Check for relevant sources
        foreach (var source in relevantSources)
        {
            if (entry.Source.Contains(source, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Filter by specific event IDs that indicate system changes
        var relevantEventIds = new HashSet<long>
        {
            7035, 7036, 7040, 7045, // Service events
            4624, 4625, 4648, 4672, // Logon events
            4688, 4689, // Process creation/termination
            4697, // Service installation
            1074, 6005, 6006, 6008, // Shutdown/startup events
            19, 20, 21, 22, 23, 24, 25, // Windows Update events
            104, // Event log cleared
            1102, // Audit log cleared
        };

        return relevantEventIds.Contains(entry.InstanceId);
    }

    private static string TruncateMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "(No message)";
        message = message.Replace("\r\n", " ").Replace("\n", " ").Trim();
        return message.Length > 500 ? message[..500] + "..." : message;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logManager.Log("WindowsEvents", "WindowsEventMonitor", "Stopping Windows Event monitoring...", LogLevel.Info);

        foreach (var log in _eventLogs)
        {
            try
            {
                log.EnableRaisingEvents = false;
                log.Dispose();
            }
            catch { }
        }

        _eventLogs.Clear();
        _logManager.Log("WindowsEvents", "WindowsEventMonitor", "Windows Event monitoring stopped", LogLevel.Info);
    }
}
