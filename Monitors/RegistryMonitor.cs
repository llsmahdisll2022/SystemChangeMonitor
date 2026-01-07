using Microsoft.Win32;
using System.Runtime.InteropServices;
using SystemChangeMonitor.Logging;

namespace SystemChangeMonitor.Monitors;

/// <summary>
/// Monitors Windows Registry changes for system settings, features, and configuration changes.
/// </summary>
public sealed class RegistryMonitor : IDisposable
{
    private readonly LogManager _logManager;
    private readonly List<RegistryKeyWatcher> _watchers;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private bool _disposed;

    // Critical registry paths to monitor for system changes
    private static readonly (RegistryHive Hive, string SubKey, string Description)[] MonitoredKeys = new[]
    {
        // System-wide settings
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies", "System Policies"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows", "Windows Group Policies"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "System Startup Programs"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "System RunOnce"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", "Windows Settings"),
        (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services", "System Services"),
        (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control", "System Control Settings"),
        (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager", "Session Manager"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "Installed Programs"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Winlogon Settings"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "Explorer Settings"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing", "Windows Features"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\State", "Windows Setup State"),
        
        // Security settings
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows Defender", "Windows Defender"),
        (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "Local Security Authority"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication", "Authentication Settings"),
        
        // Network settings
        (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TCP/IP Settings"),
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList", "Network Profiles"),
        (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "DNS Cache Settings"),
        
        // User-specific settings (Current User)
        (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "User Startup Programs"),
        (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", "User RunOnce"),
        (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies", "User Policies"),
        (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Explorer Advanced Settings"),
        (RegistryHive.CurrentUser, @"Control Panel\Desktop", "Desktop Settings"),
        (RegistryHive.CurrentUser, @"Control Panel\Mouse", "Mouse Settings"),
        (RegistryHive.CurrentUser, @"Control Panel\Keyboard", "Keyboard Settings"),
        (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes", "Theme Settings"),
        (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings", "Internet Settings"),
    };

    public RegistryMonitor(LogManager logManager)
    {
        _logManager = logManager;
        _watchers = new List<RegistryKeyWatcher>();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    public void Start()
    {
        _logManager.Log("Registry", "RegistryMonitor", "Starting Registry monitoring...", LogLevel.Info);

        foreach (var (hive, subKey, description) in MonitoredKeys)
        {
            try
            {
                var watcher = new RegistryKeyWatcher(hive, subKey, description, _logManager, _cancellationTokenSource.Token);
                if (watcher.Start())
                {
                    _watchers.Add(watcher);
                    _logManager.Log("Registry", "RegistryMonitor", $"Monitoring: {description} ({hive}\\{subKey})", LogLevel.Debug);
                }
            }
            catch (Exception ex)
            {
                _logManager.Log("Registry", "RegistryMonitor", $"Failed to monitor {description}: {ex.Message}", LogLevel.Warning);
            }
        }

        _logManager.Log("Registry", "RegistryMonitor", $"Registry monitoring started with {_watchers.Count} watchers", LogLevel.Info);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logManager.Log("Registry", "RegistryMonitor", "Stopping Registry monitoring...", LogLevel.Info);

        _cancellationTokenSource.Cancel();

        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.Dispose();
            }
            catch { }
        }

        _watchers.Clear();
        _cancellationTokenSource.Dispose();

        _logManager.Log("Registry", "RegistryMonitor", "Registry monitoring stopped", LogLevel.Info);
    }
}

/// <summary>
/// Watches a specific registry key for changes using native Windows API.
/// </summary>
internal sealed class RegistryKeyWatcher : IDisposable
{
    private readonly RegistryHive _hive;
    private readonly string _subKey;
    private readonly string _description;
    private readonly LogManager _logManager;
    private readonly CancellationToken _cancellationToken;
    private RegistryKey? _registryKey;
    private Task? _watchTask;
    private bool _disposed;

    // Win32 API imports for registry notification
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
        IntPtr hKey,
        bool bWatchSubtree,
        RegNotifyFilter dwNotifyFilter,
        IntPtr hEvent,
        bool fAsynchronous);

    [Flags]
    private enum RegNotifyFilter
    {
        Name = 0x1,
        Attributes = 0x2,
        LastSet = 0x4,
        Security = 0x8,
        ThreadAgnostic = 0x10000000
    }

    public RegistryKeyWatcher(RegistryHive hive, string subKey, string description, LogManager logManager, CancellationToken cancellationToken)
    {
        _hive = hive;
        _subKey = subKey;
        _description = description;
        _logManager = logManager;
        _cancellationToken = cancellationToken;
    }

    public bool Start()
    {
        try
        {
            var baseKey = _hive switch
            {
                RegistryHive.LocalMachine => Registry.LocalMachine,
                RegistryHive.CurrentUser => Registry.CurrentUser,
                RegistryHive.ClassesRoot => Registry.ClassesRoot,
                RegistryHive.Users => Registry.Users,
                _ => null
            };

            if (baseKey == null) return false;

            _registryKey = baseKey.OpenSubKey(_subKey, false);
            if (_registryKey == null) return false;

            _watchTask = Task.Run(WatchForChangesAsync, _cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task WatchForChangesAsync()
    {
        var keyPath = $"{_hive}\\{_subKey}";

        // Use an async registry notification with an event handle so we can stop immediately.
        using var changeEvent = new EventWaitHandle(false, EventResetMode.AutoReset);

        while (!_cancellationToken.IsCancellationRequested && !_disposed)
        {
            try
            {
                if (_registryKey == null) break;

                var handle = _registryKey.Handle.DangerousGetHandle();
                var filter = RegNotifyFilter.Name | RegNotifyFilter.Attributes | RegNotifyFilter.LastSet | RegNotifyFilter.Security;

                // Register async notification.
                var result = RegNotifyChangeKeyValue(handle, true, filter, changeEvent.SafeWaitHandle.DangerousGetHandle(), true);
                if (result != 0)
                {
                    // Non-zero = error. Don't hot loop.
                    await Task.Delay(2000, _cancellationToken);
                    continue;
                }

                var signaledIndex = WaitHandle.WaitAny(new WaitHandle[] { _cancellationToken.WaitHandle, changeEvent });
                if (signaledIndex == 0) break; // cancellation

                _logManager.LogRegistryEvent(keyPath, "Modified", details: $"Change detected in {_description}");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_cancellationToken.IsCancellationRequested)
                {
                    _logManager.Log("Registry", "RegistryKeyWatcher", $"Error watching {keyPath}: {ex.Message}", LogLevel.Warning);
                    await Task.Delay(5000, _cancellationToken);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _registryKey?.Dispose();
        }
        catch { }
    }
}
