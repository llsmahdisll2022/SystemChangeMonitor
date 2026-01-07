using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SystemChangeMonitor.Logging;

namespace SystemChangeMonitor.Monitors;

/// <summary>
/// Monitors system settings changes using WMI and Windows API.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemSettingsMonitor : IDisposable
{
    private readonly LogManager _logManager;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly List<ManagementEventWatcher> _wmiWatchers;
    private Task? _pollingTask;
    private bool _disposed;

    // Cached system state for comparison
    private Dictionary<string, string> _cachedSettings;

    public SystemSettingsMonitor(LogManager logManager)
    {
        _logManager = logManager;
        _cancellationTokenSource = new CancellationTokenSource();
        _wmiWatchers = new List<ManagementEventWatcher>();
        _cachedSettings = new Dictionary<string, string>();
    }

    public void Start()
    {
        _logManager.Log("Settings", "SystemSettingsMonitor", "Starting System Settings monitoring...", LogLevel.Info);

        // Capture initial state
        CaptureSystemState();

        // Start WMI watchers
        StartWmiWatchers();

        // Start polling task for settings that can't be watched via WMI
        _pollingTask = Task.Run(PollSystemSettingsAsync, _cancellationTokenSource.Token);

        _logManager.Log("Settings", "SystemSettingsMonitor", "System Settings monitoring started", LogLevel.Info);
    }

    private void CaptureSystemState()
    {
        try
        {
            // Capture various system settings
            _cachedSettings["TimeZone"] = TimeZoneInfo.Local.Id;
            _cachedSettings["MachineName"] = Environment.MachineName;
            _cachedSettings["OSVersion"] = Environment.OSVersion.ToString();
            _cachedSettings["ProcessorCount"] = Environment.ProcessorCount.ToString();
            _cachedSettings["SystemPageSize"] = Environment.SystemPageSize.ToString();
            _cachedSettings["Is64BitOS"] = Environment.Is64BitOperatingSystem.ToString();
            _cachedSettings["UserDomainName"] = Environment.UserDomainName;

            // Get display settings
            try
            {
                var devMode = new DEVMODE();
                devMode.dmSize = (short)Marshal.SizeOf(devMode);
                if (EnumDisplaySettings(null, -1, ref devMode))
                {
                    _cachedSettings["ScreenWidth"] = devMode.dmPelsWidth.ToString();
                    _cachedSettings["ScreenHeight"] = devMode.dmPelsHeight.ToString();
                    _cachedSettings["RefreshRate"] = devMode.dmDisplayFrequency.ToString();
                    _cachedSettings["BitsPerPixel"] = devMode.dmBitsPerPel.ToString();
                }
            }
            catch { }

            // Get power settings
            try
            {
                GetSystemPowerStatus(out var powerStatus);
                _cachedSettings["ACLineStatus"] = powerStatus.ACLineStatus.ToString();
                _cachedSettings["BatteryFlag"] = powerStatus.BatteryFlag.ToString();
            }
            catch { }
        }
        catch (Exception ex)
        {
            _logManager.Log("Settings", "SystemSettingsMonitor", $"Error capturing system state: {ex.Message}", LogLevel.Warning);
        }
    }

    private void StartWmiWatchers()
    {
        // WMI queries for monitoring various system changes
        var wmiQueries = new[]
        {
            // Process creation/termination
            ("SELECT * FROM __InstanceCreationEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_Process'", "ProcessCreated"),
            ("SELECT * FROM __InstanceDeletionEvent WITHIN 1 WHERE TargetInstance ISA 'Win32_Process'", "ProcessTerminated"),
            
            // Service changes
            ("SELECT * FROM __InstanceModificationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_Service'", "ServiceModified"),
            
            // User account changes
            ("SELECT * FROM __InstanceOperationEvent WITHIN 5 WHERE TargetInstance ISA 'Win32_UserAccount'", "UserAccountChanged"),
            
            // Network adapter changes
            ("SELECT * FROM __InstanceModificationEvent WITHIN 5 WHERE TargetInstance ISA 'Win32_NetworkAdapterConfiguration'", "NetworkConfigChanged"),
            
            // Disk changes
            ("SELECT * FROM __InstanceOperationEvent WITHIN 5 WHERE TargetInstance ISA 'Win32_LogicalDisk'", "DiskChanged"),
            
            // Environment variable changes
            ("SELECT * FROM __InstanceModificationEvent WITHIN 10 WHERE TargetInstance ISA 'Win32_Environment'", "EnvironmentChanged"),
        };

        foreach (var (query, eventType) in wmiQueries)
        {
            try
            {
                var watcher = new ManagementEventWatcher(new WqlEventQuery(query));
                watcher.EventArrived += (sender, args) => OnWmiEvent(eventType, args);
                watcher.Start();
                _wmiWatchers.Add(watcher);
            }
            catch (Exception ex)
            {
                _logManager.Log("Settings", "SystemSettingsMonitor", $"Failed to start WMI watcher for {eventType}: {ex.Message}", LogLevel.Warning);
            }
        }
    }

    private void OnWmiEvent(string eventType, EventArrivedEventArgs args)
    {
        if (_disposed) return;

        try
        {
            var targetInstance = args.NewEvent["TargetInstance"] as ManagementBaseObject;
            var details = new Dictionary<string, string> { ["EventType"] = eventType };

            if (targetInstance != null)
            {
                foreach (var prop in targetInstance.Properties)
                {
                    if (prop.Value != null && IsRelevantProperty(prop.Name))
                    {
                        details[prop.Name] = prop.Value.ToString() ?? "";
                    }
                }
            }

            var message = eventType switch
            {
                "ProcessCreated" => $"Process started: {details.GetValueOrDefault("Name", "Unknown")}",
                "ProcessTerminated" => $"Process ended: {details.GetValueOrDefault("Name", "Unknown")}",
                "ServiceModified" => $"Service changed: {details.GetValueOrDefault("Name", "Unknown")}",
                "UserAccountChanged" => $"User account changed: {details.GetValueOrDefault("Name", "Unknown")}",
                "NetworkConfigChanged" => $"Network configuration changed",
                "DiskChanged" => $"Disk changed: {details.GetValueOrDefault("DeviceID", "Unknown")}",
                "EnvironmentChanged" => $"Environment variable changed: {details.GetValueOrDefault("Name", "Unknown")}",
                _ => $"System event: {eventType}"
            };

            _logManager.Log("Settings", "WMI", message, LogLevel.Info, details);
        }
        catch (Exception ex)
        {
            _logManager.Log("Settings", "SystemSettingsMonitor", $"Error processing WMI event: {ex.Message}", LogLevel.Warning);
        }
    }

    private static bool IsRelevantProperty(string propName)
    {
        var relevantProps = new[] { "Name", "ProcessId", "CommandLine", "DeviceID", "State", "Status", "Caption", "Description" };
        return relevantProps.Contains(propName, StringComparer.OrdinalIgnoreCase);
    }

    private async Task PollSystemSettingsAsync()
    {
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, _cancellationTokenSource.Token); // Poll every 5 seconds

                // Check timezone changes
                var currentTimeZone = TimeZoneInfo.Local.Id;
                if (_cachedSettings.TryGetValue("TimeZone", out var cachedTz) && cachedTz != currentTimeZone)
                {
                    _logManager.LogSettingChange("System", "TimeZone", cachedTz, currentTimeZone);
                    _cachedSettings["TimeZone"] = currentTimeZone;
                }

                // Check display settings
                try
                {
                    var devMode = new DEVMODE();
                    devMode.dmSize = (short)Marshal.SizeOf(devMode);
                    if (EnumDisplaySettings(null, -1, ref devMode))
                    {
                        CheckAndLogChange("ScreenWidth", devMode.dmPelsWidth.ToString(), "Display");
                        CheckAndLogChange("ScreenHeight", devMode.dmPelsHeight.ToString(), "Display");
                        CheckAndLogChange("RefreshRate", devMode.dmDisplayFrequency.ToString(), "Display");
                    }
                }
                catch { }

                // Check power settings
                try
                {
                    GetSystemPowerStatus(out var powerStatus);
                    CheckAndLogChange("ACLineStatus", powerStatus.ACLineStatus.ToString(), "Power");
                }
                catch { }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logManager.Log("Settings", "SystemSettingsMonitor", $"Error polling settings: {ex.Message}", LogLevel.Warning);
            }
        }
    }

    private void CheckAndLogChange(string settingName, string currentValue, string category)
    {
        if (_cachedSettings.TryGetValue(settingName, out var cachedValue) && cachedValue != currentValue)
        {
            _logManager.LogSettingChange(category, settingName, cachedValue, currentValue);
            _cachedSettings[settingName] = currentValue;
        }
        else if (!_cachedSettings.ContainsKey(settingName))
        {
            _cachedSettings[settingName] = currentValue;
        }
    }

    // P/Invoke declarations
    [DllImport("user32.dll")]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logManager.Log("Settings", "SystemSettingsMonitor", "Stopping System Settings monitoring...", LogLevel.Info);

        _cancellationTokenSource.Cancel();

        foreach (var watcher in _wmiWatchers)
        {
            try
            {
                watcher.Stop();
                watcher.Dispose();
            }
            catch { }
        }

        _wmiWatchers.Clear();

        try
        {
            _pollingTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch { }

        _cancellationTokenSource.Dispose();

        _logManager.Log("Settings", "SystemSettingsMonitor", "System Settings monitoring stopped", LogLevel.Info);
    }
}
