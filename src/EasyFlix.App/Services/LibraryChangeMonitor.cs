using System.IO;
using EasyFlix.Core;
using EasyFlix.Core.Services;

namespace EasyFlix.App.Services;

public sealed class LibraryChangeMonitor : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly IReadOnlyList<string> _roots;
    private readonly Dictionary<string, bool> _rootPresence = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _timer;
    private readonly Timer _rootProbeTimer;
    private readonly TimeSpan _debounce;
    private readonly object _watcherLock = new();
    private int _probeActive;
    private bool _disposed;

    public LibraryChangeMonitor(IEnumerable<string> roots, TimeSpan? debounce = null)
    {
        _debounce = debounce ?? TimeSpan.FromSeconds(2);
        _timer = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _roots = NormalizeRoots(roots);
        foreach (var root in _roots)
            _rootPresence[root] = Directory.Exists(root);

        RebuildWatchers();
        _rootProbeTimer = new Timer(
            _ => ProbeRootPresence(),
            null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));
    }

    public event EventHandler? Changed;

    private void RebuildWatchers()
    {
        lock (_watcherLock)
        {
            foreach (var watcher in _watchers)
                watcher.Dispose();
            _watchers.Clear();

            foreach (var root in _roots)
            {
                if (!Directory.Exists(root) || EasyFlixSafetyPolicy.IsHardExcluded(root)) continue;

                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };
                    watcher.Changed += OnChanged;
                    watcher.Created += OnChanged;
                    watcher.Deleted += OnChanged;
                    watcher.Renamed += OnRenamed;
                    watcher.Error += OnError;
                    _watchers.Add(watcher);
                }
                catch
                {
                    // A watcher is only a hint. Reconciliation remains the source of truth.
                }
            }
        }
    }

    private void ProbeRootPresence()
    {
        if (_disposed || Interlocked.Exchange(ref _probeActive, 1) != 0) return;
        try
        {
            var changed = false;
            foreach (var root in _roots)
            {
                var present = Directory.Exists(root);
                if (_rootPresence.TryGetValue(root, out var previous) && previous == present) continue;
                _rootPresence[root] = present;
                changed = true;
            }

            if (!changed || _disposed) return;
            RebuildWatchers();
            Debounce();
        }
        finally
        {
            Volatile.Write(ref _probeActive, 0);
        }
    }

    private static IReadOnlyList<string> NormalizeRoots(IEnumerable<string> roots)
    {
        var values = new List<string>();
        foreach (var rawRoot in roots.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            try
            {
                var root = PathSafety.Normalize(rawRoot);
                if (!EasyFlixSafetyPolicy.IsHardExcluded(root))
                    values.Add(root);
            }
            catch
            {
            }
        }
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Debounce();

    private void OnRenamed(object sender, RenamedEventArgs e) => Debounce();

    private void OnError(object sender, ErrorEventArgs e) => Debounce();

    private void Debounce()
    {
        if (_disposed) return;
        _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rootProbeTimer.Dispose();
        _timer.Dispose();
        lock (_watcherLock)
        {
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
        }
    }
}
