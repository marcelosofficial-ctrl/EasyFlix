using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EasyFlix.Core.Services;

namespace EasyFlix.App.Services;

public sealed class HistoricalCatalogBootstrapService
{
    private readonly HistoricalCatalogStore _store;
    private readonly string _manifestPath;

    public HistoricalCatalogBootstrapService(CatalogStore catalogStore, string? manifestPath = null)
    {
        _store = new HistoricalCatalogStore(catalogStore);
        _manifestPath = manifestPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyFlix", "bootstrap", "historical-offline.json");
    }

    public async Task<int> ImportIfPresentAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_manifestPath)) return 0;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());

        var json = await File.ReadAllTextAsync(_manifestPath, cancellationToken);
        var manifest = JsonSerializer.Deserialize<Manifest>(json, options)
            ?? throw new InvalidDataException("Historical offline manifest could not be parsed.");
        if (manifest.Version != 1)
            throw new InvalidDataException($"Unsupported historical manifest version: {manifest.Version}");

        return await _store.ImportOfflineAsync(manifest.Titles ?? [], cancellationToken);
    }

    private sealed record Manifest(int Version, HistoricalCatalogEntry[]? Titles);
}
