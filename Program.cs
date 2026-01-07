using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SystemChangeMonitor.Logging;
using SystemChangeMonitor.Monitors;

namespace SystemChangeMonitor;

/// <summary>
/// System Change Monitor - A comprehensive Windows system monitoring application
/// that captures and logs all minor system changes including file operations,
/// registry changes, service status, settings modifications, and Windows events.
/// </summary>
[SupportedOSPlatform("windows")]
internal class Program
{
    private static LogManager? _logManager;
    private static FileSystemMonitor? _fileSystemMonitor;
    private static WindowsEventMonitor? _windowsEventMonitor;
    private static RegistryMonitor? _registryMonitor;
    private static ServiceMonitor? _serviceMonitor;
    private static SystemSettingsMonitor? _systemSettingsMonitor;
    private static readonly CancellationTokenSource _cancellationTokenSource = new();
    private static bool _isShuttingDown;

    // Console control handler delegate
    private delegate bool ConsoleCtrlDelegate(int ctrlType);
    private static ConsoleCtrlDelegate? _consoleCtrlHandler;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, bool add);

    private const int CTRL_C_EVENT = 0;
    private const int CTRL_BREAK_EVENT = 1;
    private const int CTRL_CLOSE_EVENT = 2;
    private const int CTRL_LOGOFF_EVENT = 5;
    private const int CTRL_SHUTDOWN_EVENT = 6;

    static async Task<int> Main(string[] args)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine("This application is designed for Windows only.");
            return 1;
        }

        Console.Title = "System Change Monitor";
        PrintBanner();

        // Check for admin privileges
        if (!IsRunningAsAdministrator())
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("WARNING: Running without administrator privileges.");
            Console.WriteLine("Some monitoring features may be limited.");
            Console.WriteLine("For full functionality, please run as Administrator.");
            Console.ResetColor();
            Console.WriteLine();
        }

        // Set up console control handler for graceful shutdown
        _consoleCtrlHandler = ConsoleCtrlHandler;
        SetConsoleCtrlHandler(_consoleCtrlHandler, true);

        // Also handle Ctrl+C through .NET
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true; // Prevent immediate termination
            InitiateShutdown();
        };

        // Handle process exit
        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
        {
            InitiateShutdown();
        };

        try
        {
            // Initialize logging
            _logManager = new LogManager();
            Console.WriteLine($"Log files will be stored in: {_logManager.LogDirectory}");
            Console.WriteLine();

            _logManager.Log("System", "Program", "=== System Change Monitor Started ===", LogLevel.Info);
            _logManager.Log("System", "Program", $"Machine: {Environment.MachineName}", LogLevel.Info);
            _logManager.Log("System", "Program", $"OS: {Environment.OSVersion}", LogLevel.Info);
            _logManager.Log("System", "Program", $"User: {Environment.UserDomainName}\\{Environment.UserName}", LogLevel.Info);

            // Start all monitors
            StartMonitors();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("All monitors started successfully!");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("Press Ctrl+C or close the window to stop monitoring.");
            Console.WriteLine(new string('-', 60));
            Console.WriteLine();

            // Keep running until cancellation
            await Task.Delay(Timeout.Infinite, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Fatal error: {ex.Message}");
            Console.ResetColor();
            _logManager?.Log("System", "Program", $"Fatal error: {ex}", LogLevel.Error);
            return 1;
        }
        finally
        {
            Shutdown();
        }

        return 0;
    }

    private static void StartMonitors()
    {
        Console.WriteLine("Starting monitors...");
        Console.WriteLine();

        // File System Monitor
        try
        {
            Console.Write("  [1/5] File System Monitor... ");
            _fileSystemMonitor = new FileSystemMonitor(_logManager!);
            _fileSystemMonitor.Start();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("OK");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILED: {ex.Message}");
            Console.ResetColor();
        }

        // Windows Event Monitor
        try
        {
            Console.Write("  [2/5] Windows Event Monitor... ");
            _windowsEventMonitor = new WindowsEventMonitor(_logManager!);
            _windowsEventMonitor.Start();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("OK");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILED: {ex.Message}");
            Console.ResetColor();
        }

        // Registry Monitor
        try
        {
            Console.Write("  [3/5] Registry Monitor... ");
            _registryMonitor = new RegistryMonitor(_logManager!);
            _registryMonitor.Start();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("OK");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILED: {ex.Message}");
            Console.ResetColor();
        }

        // Service Monitor
        try
        {
            Console.Write("  [4/5] Service Monitor... ");
            _serviceMonitor = new ServiceMonitor(_logManager!);
            _serviceMonitor.Start();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("OK");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILED: {ex.Message}");
            Console.ResetColor();
        }

        // System Settings Monitor
        try
        {
            Console.Write("  [5/5] System Settings Monitor... ");
            _systemSettingsMonitor = new SystemSettingsMonitor(_logManager!);
            _systemSettingsMonitor.Start();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("OK");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAILED: {ex.Message}");
            Console.ResetColor();
        }

        Console.WriteLine();
    }

    private static bool ConsoleCtrlHandler(int ctrlType)
    {
        switch (ctrlType)
        {
            case CTRL_C_EVENT:
            case CTRL_BREAK_EVENT:
            case CTRL_CLOSE_EVENT:
            case CTRL_LOGOFF_EVENT:
            case CTRL_SHUTDOWN_EVENT:
                InitiateShutdown();
                // Return immediately; monitors will stop as soon as cancellation is observed.
                return true;
            default:
                return false;
        }
    }

    private static void InitiateShutdown()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("Shutdown requested. Stopping monitors...");
        Console.ResetColor();

        try
        {
            _cancellationTokenSource.Cancel();
        }
        catch { }
    }

    private static void Shutdown()
    {
        Console.WriteLine();
        Console.WriteLine("Cleaning up resources...");

        _logManager?.Log("System", "Program", "=== System Change Monitor Stopping ===", LogLevel.Info);

        // Dispose monitors in reverse order
        try { _systemSettingsMonitor?.Dispose(); } catch { }
        try { _serviceMonitor?.Dispose(); } catch { }
        try { _registryMonitor?.Dispose(); } catch { }
        try { _windowsEventMonitor?.Dispose(); } catch { }
        try { _fileSystemMonitor?.Dispose(); } catch { }

        _logManager?.Log("System", "Program", "=== System Change Monitor Stopped ===", LogLevel.Info);

        try { _logManager?.Dispose(); } catch { }
        try { _cancellationTokenSource.Dispose(); } catch { }

        // Remove console control handler
        SetConsoleCtrlHandler(_consoleCtrlHandler, false);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Shutdown complete. All logs saved.");
        Console.ResetColor();
    }

    private static bool IsRunningAsAdministrator()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════╗
║           SYSTEM CHANGE MONITOR for Windows                  ║
║          Comprehensive System Activity Logging               ║
╠══════════════════════════════════════════════════════════════╣
║  Monitors:                                                   ║
║    • File System Changes (create, modify, delete, rename)    ║
║    • Windows Event Logs (system, security, application)      ║
║    • Registry Modifications (settings, policies, features)   ║
║    • Service State Changes (start, stop, install)            ║
║    • System Settings (display, power, network, etc.)         ║
╚══════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
}
