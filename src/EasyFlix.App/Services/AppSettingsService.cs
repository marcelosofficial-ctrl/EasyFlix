using System.Text.Json;
using System.IO;
using EasyFlix.Core;

namespace EasyFlix.App.Services;

public sealed record AppSettings(string SourceRoots, string ExcludedPaths, string LibraryPath);

public sealed class AppSettingsService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyFlix", "settings.json");

    private readonly string _legacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReelShelf", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (TryLoad(_path, out var current))
            {
                ApplySafetyPolicy(current);
                return current;
            }

            if (TryLoad(_legacyPath, out var legacy))
            {
                Save(legacy);
                return legacy;
            }
        }
        catch { }

        var defaults = Defaults();
        ApplySafetyPolicy(defaults);
        return defaults;
    }

    public void Save(AppSettings settings)
    {
        ApplySafetyPolicy(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool TryLoad(string path, out AppSettings settings)
    {
        settings = Defaults();
        if (!File.Exists(path))
            return false;

        var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
        if (loaded is null)
            return false;

        settings = loaded;
        return true;
    }

    private static void ApplySafetyPolicy(AppSettings settings)
    {
        var protectedPaths = (settings.ExcludedPaths ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        EasyFlixSafetyPolicy.ConfigureProtectedPaths(protectedPaths);
    }

    private static AppSettings Defaults() => new(
        SourceRoots: string.Empty,
        ExcludedPaths: string.Empty,
        LibraryPath: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Movie Library"));
}
