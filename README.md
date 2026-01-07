# SystemChangeMonitor

Version: 1.0.0  
Default branch: `main`

SystemChangeMonitor is a C# console application for Windows and Windows Server that logs very small system changes. The application records system events such as feature/service enablement or disablement, switch setting changes, and file system events (create, modify, move/rename, delete). Logs are written to files located next to the application (configurable). Monitoring runs while the console is open and stops when the console is closed.

## Features
- Monitors file and directory events: created, modified, moved/renamed, deleted
- Logs service/feature enablement and disablement events
- Records registry and other system-level changes (when enabled)
- Writes structured logs suitable for human reading and automated processing
- Configurable log path, log level, filters, and retention policies
- Stops monitoring on console exit; intended behavior for interactive use

## Requirements
- .NET (specify the project's target framework, e.g., .NET 6 or .NET 7)
- Windows 10 / Windows Server 2016 or newer (or the versions the project targets)
- Administrator privileges recommended for full monitoring coverage

## Installation
Clone the repository and build:
```bash
git clone https://github.com/llsmahdisll2022/SystemChangeMonitor.git
cd SystemChangeMonitor
dotnet build -c Release
```

## Run
Run from the project folder:
```bash
dotnet run --project src/SystemChangeMonitor
```
Or run the compiled executable:
```powershell
.\bin\Release\net6.0\SystemChangeMonitor.exe
```
Example runtime options (if supported):
```powershell
SystemChangeMonitor.exe --logPath "C:\Logs\SysMon" --logLevel Information
```

## Configuration
Use a JSON configuration (e.g., `appsettings.json`) to set log path, log level, watched paths, excluded patterns, and enabled monitors. Example structure:
```json
{
  "Log": {
    "Path": "logs",
    "Level": "Information",
    "MaxFileSizeMB": 50,
    "RetainedFileCount": 10,
    "Format": "json"
  },
  "Watch": {
    "Paths": [
      "C:\\Users",
      "C:\\Program Files"
    ],
    "Exclude": [
      "**\\Temp\\**",
      "**\\.git\\**"
    ]
  },
  "Features": {
    "MonitorFileSystem": true,
    "MonitorRegistry": true,
    "MonitorServices": true,
    "MonitorWMI": true
  }
}
```

## Log format (recommended)
Use structured JSON logs with fields:
- timestamp (ISO 8601)
- eventType (FileCreated, FileDeleted, FileMoved, ServiceChanged, RegistryChanged, ...)
- path (when applicable)
- oldPath / newPath (for moves/renames)
- username
- processName / pid (if available)
- details (object or string with extra context)

Example:
```json
{
  "timestamp": "2026-01-07T12:34:56Z",
  "eventType": "FileDeleted",
  "path": "C:\\Important\\secret.txt",
  "username": "DESKTOP\\admin",
  "processName": "explorer.exe",
  "details": {
    "reason": "deleted by user"
  }
}
```

## How it works (technical overview)
Recommended approaches for comprehensive monitoring:
- FileSystemWatcher for filesystem events (with fallback scans to cover missed events)
- WMI event subscriptions for system-level changes and service events
- Registry change notifications (RegNotifyChangeKeyValue or equivalent)
- Optional ETW integration for higher-fidelity system tracing
Design should use CancellationToken and graceful shutdown to flush logs when console exits.

## Log management & retention
- Implement rotation by size or date
- Enforce retention policy (e.g., keep last N files or X days)
- Compress archived logs to save space
- Monitor disk usage and handle low-disk conditions (alert or pause logging)

## Running continuously
For continuous production monitoring, run as a Windows Service or use a wrapper to detach from an interactive console. This prevents monitoring from stopping when the console is closed.

## Development & architecture notes
- Separate concerns: monitoring layers, logging service, configuration, and host lifecycle
- Use async/await and CancellationToken for responsive shutdown
- Recommended libraries: Serilog (structured logging), Microsoft.Extensions.Configuration, Microsoft.Extensions.Hosting
- Unit and integration tests for monitoring behaviors and performance under load are recommended

## Security & permissions
- Administrator rights are often required to access protected system areas and events
- Logs may contain sensitive information; restrict access using NTFS permissions
- Consider encrypting or signing logs if tamper-resistance is required

## Troubleshooting
- Missing events: verify privileges, ensure paths are watched, and consider periodic scans
- High resource usage: narrow watched paths, add batching, reduce detail level
- Logs not written: check log path permissions and runtime errors printed to console

## Roadmap
- Add Windows Service host option
- Provide installer or MSI
- Add a web viewer or REST API for remote log access
- Add log integrity protections (signing/checksums)
- Add alerting (email/webhook) for critical events
- Add automated tests and CI

## License
