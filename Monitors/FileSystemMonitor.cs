using SystemChangeMonitor.Logging;

namespace SystemChangeMonitor.Monitors;

/// <summary>
/// Monitors file system changes across multiple drives and critical system directories.
/// </summary>
public sealed class FileSystemMonitor : IDisposable
{
    private readonly LogManager _logManager;
    private readonly List<FileSystemWatcher> _watchers;
    private readonly HashSet<string> _recentEvents;
    private readonly object _eventLock = new();
    private bool _disposed;

    public FileSystemMonitor(LogManager logManager)
    {
        _logManager = logManager;
        _watchers = new List<FileSystemWatcher>();
        _recentEvents = new HashSet<string>();
    }

    /// <summary>
    /// Starts monitoring file system changes on all available drives and critical directories.
    /// </summary>
    public void Start()
    {
        _logManager.Log("FileSystem", "FileSystemMonitor", "Starting file system monitoring...", LogLevel.Info);

        // Monitor all fixed drives
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
            .ToList();

        foreach (var drive in drives)
        {
            try
            {
                CreateWatcher(drive.RootDirectory.FullName, "*.*", true);
                _logManager.Log("FileSystem", "FileSystemMonitor", $"Monitoring drive: {drive.Name}", LogLevel.Info);
            }
            catch (Exception ex)
            {
                _logManager.Log("FileSystem", "FileSystemMonitor", $"Failed to monitor drive {drive.Name}: {ex.Message}", LogLevel.Warning);
            }
        }

        // Monitor critical system directories with higher priority
        var criticalPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "config"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64"),
        };

        foreach (var path in criticalPaths.Where(Directory.Exists).Distinct())
        {
            try
            {
                // Create additional watchers for critical paths with specific filters
                CreateWatcher(path, "*.exe", false);
                CreateWatcher(path, "*.dll", false);
                CreateWatcher(path, "*.sys", false);
                CreateWatcher(path, "*.ini", false);
                CreateWatcher(path, "*.config", false);
            }
            catch (Exception ex)
            {
                _logManager.Log("FileSystem", "FileSystemMonitor", $"Failed to monitor critical path {path}: {ex.Message}", LogLevel.Warning);
            }
        }

        _logManager.Log("FileSystem", "FileSystemMonitor", $"File system monitoring started with {_watchers.Count} watchers", LogLevel.Info);
    }

    private void CreateWatcher(string path, string filter, bool includeSubdirectories)
    {
        if (!Directory.Exists(path)) return;

        var watcher = new FileSystemWatcher(path)
        {
            Filter = filter,
            IncludeSubdirectories = includeSubdirectories,
            EnableRaisingEvents = false,
            NotifyFilter = NotifyFilters.FileName |
                          NotifyFilters.DirectoryName |
                          NotifyFilters.LastWrite |
                          NotifyFilters.CreationTime |
                          NotifyFilters.Size |
                          NotifyFilters.Attributes |
                          NotifyFilters.Security,
            InternalBufferSize = 65536 // 64KB buffer for high-volume scenarios
        };

        watcher.Created += OnFileCreated;
        watcher.Deleted += OnFileDeleted;
        watcher.Changed += OnFileChanged;
        watcher.Renamed += OnFileRenamed;
        watcher.Error += OnWatcherError;

        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        if (!TryAddRecentEvent($"Created:{e.FullPath}")) return;

        var details = GetFileDetails(e.FullPath);
        _logManager.LogFileSystemEvent("Created", e.FullPath, details: details);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        if (!TryAddRecentEvent($"Deleted:{e.FullPath}")) return;

        // File/dir no longer exists; log as much context as we can from the path.
        var details = $"Name={Path.GetFileName(e.FullPath)}, Parent={Path.GetDirectoryName(e.FullPath)}, Extension={Path.GetExtension(e.FullPath)}";
        _logManager.LogFileSystemEvent("Deleted", e.FullPath, details: details);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        if (!TryAddRecentEvent($"Changed:{e.FullPath}")) return;

        var details = GetFileDetails(e.FullPath);
        var extra = string.IsNullOrWhiteSpace(details)
            ? $"ChangeType={e.ChangeType}"
            : $"ChangeType={e.ChangeType}, {details}";

        _logManager.LogFileSystemEvent("Modified", e.FullPath, details: extra);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldIgnore(e.FullPath) && ShouldIgnore(e.OldFullPath)) return;
        if (!TryAddRecentEvent($"Renamed:{e.OldFullPath}:{e.FullPath}")) return;

        var oldDir = Path.GetDirectoryName(e.OldFullPath) ?? "";
        var newDir = Path.GetDirectoryName(e.FullPath) ?? "";
        var isMove = !string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase);
        var eventType = isMove ? "Moved" : "Renamed";

        var details = GetFileDetails(e.FullPath);
        var extra = $"OldName={Path.GetFileName(e.OldFullPath)}, NewName={Path.GetFileName(e.FullPath)}, OldDir={oldDir}, NewDir={newDir}";
        if (!string.IsNullOrWhiteSpace(details)) extra += $", {details}";

        _logManager.LogFileSystemEvent(eventType, e.FullPath, e.OldFullPath, extra);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        _logManager.Log("FileSystem", "FileSystemMonitor", $"Watcher error: {ex.Message}", LogLevel.Error);

        // Try to restart the watcher
        if (sender is FileSystemWatcher watcher)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.EnableRaisingEvents = true;
                _logManager.Log("FileSystem", "FileSystemMonitor", "Watcher restarted after error", LogLevel.Info);
            }
            catch (Exception restartEx)
            {
                _logManager.Log("FileSystem", "FileSystemMonitor", $"Failed to restart watcher: {restartEx.Message}", LogLevel.Error);
            }
        }
    }

    private bool ShouldIgnore(string path)
    {
        // Ignore our own log files
        if (path.Contains(_logManager.LogDirectory, StringComparison.OrdinalIgnoreCase))
            return true;

        // Ignore temporary files and common noise
        var fileName = Path.GetFileName(path);
        // To capture as many changes as possible, keep ignores minimal and only exclude
        // extremely noisy/system-generated files.
        var ignoredPatterns = new[]
        {
            ".tmp", ".temp", "~$", ".lock",
            "thumbs.db", "desktop.ini", ".ds_store",
            "pagefile.sys", "hiberfil.sys", "swapfile.sys"
        };

        foreach (var pattern in ignoredPatterns)
        {
            if (fileName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Ignore browser cache directories
        var ignoredDirs = new[]
        {
            // Very high-volume cache locations (keep this list specific to avoid suppressing legitimate changes).
            @"\\Windows\\Prefetch",
            @"\\AppData\\Local\\Google\\Chrome\\User Data\\Default\\Cache",
            @"\\AppData\\Local\\Microsoft\\Edge\\User Data\\Default\\Cache",
            @"\\AppData\\Local\\Mozilla\\Firefox\\Profiles"
        };

        foreach (var dir in ignoredDirs)
        {
            if (path.Contains(dir, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private bool TryAddRecentEvent(string eventKey)
    {
        lock (_eventLock)
        {
            var timedKey = $"{eventKey}:{DateTime.Now:HHmmss}";

            // Clean old events (older than 1 second)
            if (_recentEvents.Count > 1000)
            {
                _recentEvents.Clear();
            }

            if (_recentEvents.Contains(timedKey))
                return false;

            _recentEvents.Add(timedKey);
            return true;
        }
    }

    private static string? GetFileDetails(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                return $"Size={info.Length}bytes, Created={info.CreationTime:yyyy-MM-dd HH:mm:ss}, Modified={info.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
            }
            else if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                return $"Directory, Created={info.CreationTime:yyyy-MM-dd HH:mm:ss}";
            }
        }
        catch
        {
            // Access denied or file no longer exists
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logManager.Log("FileSystem", "FileSystemMonitor", "Stopping file system monitoring...", LogLevel.Info);

        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
        }

        _watchers.Clear();
        _logManager.Log("FileSystem", "FileSystemMonitor", "File system monitoring stopped", LogLevel.Info);
    }
}
