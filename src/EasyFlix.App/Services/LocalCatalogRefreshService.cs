using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.App.Services;

public sealed record MediaIntakeRuntimeOptions(
    string? IntakeRoot = null,
    AppSettings? SettingsOverride = null,
    string? HistoricalManifestPath = null,
    bool EnableLegacyImport = true);
public sealed record LocalCatalogRefreshResult(
    ScanOptions ScanOptions,
    CatalogReconcileSummary? Summary,
    LegacyLibraryBridgeResult? Legacy);

public sealed class LocalCatalogRefreshService
{
    private readonly CatalogStore _store;
    private readonly AppSettingsService _settingsService = new();
    private readonly AppSettings? _settingsOverride;
    private readonly CatalogReconciler _reconciler;
    private readonly HistoricalCatalogBootstrapService _historicalBootstrap;
    private readonly LegacyLibraryImportCoordinator? _legacyLibrary;
    private readonly MediaIntakeManifestService _mediaIntake;

    public LocalCatalogRefreshService(CatalogStore store, MediaIntakeRuntimeOptions? runtimeOptions = null)
    {
        runtimeOptions ??= new MediaIntakeRuntimeOptions();
        _store = store;
        _settingsOverride = runtimeOptions.SettingsOverride;
        _reconciler = new CatalogReconciler(store);
        _historicalBootstrap = new HistoricalCatalogBootstrapService(store, runtimeOptions.HistoricalManifestPath);
        _legacyLibrary = runtimeOptions.EnableLegacyImport
            ? new LegacyLibraryImportCoordinator(store)
            : null;
        _mediaIntake = new MediaIntakeManifestService(store, runtimeOptions.IntakeRoot);
    }

    public async Task<ScanOptions> BuildScanOptionsAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsOverride ?? _settingsService.Load();
        await _store.InitializeAsync(cancellationToken);
        var configuredRoots = ParsePaths(settings.SourceRoots);
        var knownRoots = await _store.LoadKnownRootPathsAsync(cancellationToken);
        var roots = configuredRoots
            .Concat(knownRoots)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(PathSafety.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ScanOptions(
            roots,
            ParsePaths(settings.ExcludedPaths),
            settings.LibraryPath);
    }

    public async Task<LocalCatalogRefreshResult> RefreshAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var scanOptions = await BuildScanOptionsAsync(cancellationToken);
        await _historicalBootstrap.ImportIfPresentAsync(cancellationToken);

        CatalogReconcileSummary? summary = null;
        if (scanOptions.SourceRoots.Count > 0)
            summary = await _reconciler.ReconcileAsync(scanOptions, progress, cancellationToken);

        LegacyLibraryBridgeResult? legacy = null;
        if (_legacyLibrary is not null)
            legacy = await _legacyLibrary.ApplyOrImportDefaultAsync(cancellationToken);
        await _mediaIntake.ApplySavedAsync(cancellationToken);
        return new(scanOptions, summary, legacy);
    }

    private static string[] ParsePaths(string value)
        => value.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(PathSafety.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}