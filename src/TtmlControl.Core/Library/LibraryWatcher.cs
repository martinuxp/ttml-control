namespace TtmlControl.Core.Library;

/// <summary>
/// Signals that scanned folders may have changed. It carries no details on purpose: events can
/// be lost or arrive out of order, so the scanner remains the only source of truth.
/// </summary>
public sealed class LibraryWatcher(TimeSpan debounce) : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _lock = new();
    private Timer? _timer;
    private bool _disposed;

    public event Action? Changed;

    public void SetRoots(IEnumerable<string> roots)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            DisposeWatchers();
            foreach (var root in roots.Where(Directory.Exists))
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Changed += OnEvent;
                watcher.Created += OnEvent;
                watcher.Deleted += OnEvent;
                watcher.Renamed += OnRenamed;
                watcher.Error += (_, _) => Schedule();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
        }
    }

    public static bool IsRelevant(WatcherChangeTypes change, string path)
    {
        if (path.EndsWith(".ttml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A folder moving takes its files along without an event per file. A deleted path can
        // no longer be checked, so an extensionless one is assumed to have been a folder.
        return change switch
        {
            WatcherChangeTypes.Deleted => !Path.HasExtension(path),
            WatcherChangeTypes.Created or WatcherChangeTypes.Renamed => Directory.Exists(path),
            _ => false,
        };
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        if (IsRelevant(e.ChangeType, e.FullPath))
        {
            Schedule();
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsRelevant(e.ChangeType, e.FullPath) || IsRelevant(e.ChangeType, e.OldFullPath))
        {
            Schedule();
        }
    }

    // A single save raises several events, so only the quiet period after the last one counts.
    private void Schedule()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _timer ??= new Timer(_ => Changed?.Invoke());
            _timer.Change(debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisposeWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            DisposeWatchers();
            _timer?.Dispose();
        }
    }
}
