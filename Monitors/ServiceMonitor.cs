using System.ServiceProcess;
using SystemChangeMonitor.Logging;

namespace SystemChangeMonitor.Monitors;

/// <summary>
/// Monitors Windows Service state changes.
/// </summary>
public sealed class ServiceMonitor : IDisposable
{
    private readonly LogManager _logManager;
    private readonly Dictionary<string, ServiceControllerStatus> _serviceStates;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private Task? _monitorTask;
    private bool _disposed;

    public ServiceMonitor(LogManager logManager)
    {
        _logManager = logManager;
        _serviceStates = new Dictionary<string, ServiceControllerStatus>(StringComparer.OrdinalIgnoreCase);
        _cancellationTokenSource = new CancellationTokenSource();
    }

    public void Start()
    {
        _logManager.Log("Services", "ServiceMonitor", "Starting Service monitoring...", LogLevel.Info);

        // Capture initial state of all services
        CaptureServiceStates();

        // Start monitoring task
        _monitorTask = Task.Run(MonitorServicesAsync, _cancellationTokenSource.Token);

        _logManager.Log("Services", "ServiceMonitor", $"Service monitoring started, tracking {_serviceStates.Count} services", LogLevel.Info);
    }

    private void CaptureServiceStates()
    {
        try
        {
            var services = ServiceController.GetServices();
            foreach (var service in services)
            {
                try
                {
                    _serviceStates[service.ServiceName] = service.Status;
                }
                catch { }
                finally
                {
                    service.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logManager.Log("Services", "ServiceMonitor", $"Error capturing service states: {ex.Message}", LogLevel.Warning);
        }
    }

    private async Task MonitorServicesAsync()
    {
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, _cancellationTokenSource.Token); // Check every 2 seconds
                CheckForServiceChanges();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logManager.Log("Services", "ServiceMonitor", $"Error monitoring services: {ex.Message}", LogLevel.Error);
            }
        }
    }

    private void CheckForServiceChanges()
    {
        try
        {
            var currentServices = ServiceController.GetServices();
            var currentServiceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var service in currentServices)
            {
                try
                {
                    currentServiceNames.Add(service.ServiceName);

                    if (_serviceStates.TryGetValue(service.ServiceName, out var previousStatus))
                    {
                        if (previousStatus != service.Status)
                        {
                            _logManager.LogServiceEvent(
                                service.ServiceName,
                                $"{previousStatus} -> {service.Status}",
                                $"Display Name: {service.DisplayName}"
                            );
                            _serviceStates[service.ServiceName] = service.Status;
                        }
                    }
                    else
                    {
                        // New service detected
                        _logManager.LogServiceEvent(
                            service.ServiceName,
                            $"New Service Detected ({service.Status})",
                            $"Display Name: {service.DisplayName}"
                        );
                        _serviceStates[service.ServiceName] = service.Status;
                    }
                }
                catch { }
                finally
                {
                    service.Dispose();
                }
            }

            // Check for removed services
            var removedServices = _serviceStates.Keys
                .Where(name => !currentServiceNames.Contains(name))
                .ToList();

            foreach (var serviceName in removedServices)
            {
                _logManager.LogServiceEvent(serviceName, "Service Removed", "Service no longer exists");
                _serviceStates.Remove(serviceName);
            }
        }
        catch (Exception ex)
        {
            _logManager.Log("Services", "ServiceMonitor", $"Error checking service changes: {ex.Message}", LogLevel.Warning);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logManager.Log("Services", "ServiceMonitor", "Stopping Service monitoring...", LogLevel.Info);

        _cancellationTokenSource.Cancel();

        try
        {
            _monitorTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch { }

        _cancellationTokenSource.Dispose();
        _serviceStates.Clear();

        _logManager.Log("Services", "ServiceMonitor", "Service monitoring stopped", LogLevel.Info);
    }
}
