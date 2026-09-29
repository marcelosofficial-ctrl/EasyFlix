using System.Diagnostics;
using EasyFlix.Core.Models;

namespace EasyFlix.Core.Services;

public sealed record CatalogRootReconcileTiming(bool Online, TimeSpan ScanElapsed, TimeSpan SnapshotElapsed, TimeSpan CatalogWriteElapsed, int TitlesObserved, int FilesObserved);

public sealed record CatalogReconcileSummary(int RootsOnline, int RootsOffline, int TitlesObserved, int FilesObserved)
{
    public TimeSpan Elapsed { get; init; }
    public IReadOnlyList<CatalogRootReconcileTiming> RootTimings { get; init; } = [];
    public TimeSpan ScanElapsed => TimeSpan.FromTicks(RootTimings.Sum(item => item.ScanElapsed.Ticks));
    public TimeSpan SnapshotElapsed => TimeSpan.FromTicks(RootTimings.Sum(item => item.SnapshotElapsed.Ticks));
    public TimeSpan CatalogWriteElapsed => TimeSpan.FromTicks(RootTimings.Sum(item => item.CatalogWriteElapsed.Ticks));
};

public sealed class CatalogReconciler(
    CatalogStore store,
    MediaScanner? scanner = null,
    CatalogSnapshotBuilder? snapshotBuilder = null)
{
    private readonly MediaScanner _scanner = scanner ?? new MediaScanner();
    private readonly CatalogSnapshotBuilder _snapshotBuilder = snapshotBuilder ?? new CatalogSnapshotBuilder();

    public async Task<CatalogReconcileSummary> ReconcileAsync(
        ScanOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await store.InitializeAsync(cancellationToken);
        var roots = options.SourceRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathSafety.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var effectiveExclusions = EasyFlixSafetyPolicy.MergeExclusions(options.ExcludedPaths)
            .Select(PathSafety.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var totalStopwatch = Stopwatch.StartNew();
        var rootTimings = new List<CatalogRootReconcileTiming>();
        var online = 0;
        var offline = 0;
        var titles = 0;
        var files = 0;

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EasyFlixSafetyPolicy.IsHardExcluded(root) || PathSafety.IsExcluded(root, effectiveExclusions)) continue;

            if (!Directory.Exists(root))
            {
                progress?.Report($"Offline: {root}");
                var offlineWrite = Stopwatch.StartNew();
                await store.MarkRootOfflineAsync(root, cancellationToken);
                offlineWrite.Stop();
                rootTimings.Add(new CatalogRootReconcileTiming(false, TimeSpan.Zero, TimeSpan.Zero, offlineWrite.Elapsed, 0, 0));
                offline++;
                continue;
            }

            progress?.Report($"Reconciling {root}");
            var scanStopwatch = Stopwatch.StartNew();
            var found = await _scanner.ScanAsync(
                new ScanOptions([root], options.ExcludedPaths, options.LibraryPath),
                progress,
                cancellationToken);
            scanStopwatch.Stop();

            var snapshotStopwatch = Stopwatch.StartNew();
            var snapshot = _snapshotBuilder.BuildRoot(root, found);
            snapshotStopwatch.Stop();

            var writeStopwatch = Stopwatch.StartNew();
            await store.ReplaceOnlineRootAsync(snapshot, cancellationToken);
            writeStopwatch.Stop();
            rootTimings.Add(new CatalogRootReconcileTiming(true, scanStopwatch.Elapsed, snapshotStopwatch.Elapsed, writeStopwatch.Elapsed, snapshot.Titles.Count, snapshot.Files.Count));
            online++;
            titles += snapshot.Titles.Count;
            files += snapshot.Files.Count;
        }

        totalStopwatch.Stop();
        return new CatalogReconcileSummary(online, offline, titles, files)
        {
            Elapsed = totalStopwatch.Elapsed,
            RootTimings = rootTimings.ToArray()
        };
    }
}
