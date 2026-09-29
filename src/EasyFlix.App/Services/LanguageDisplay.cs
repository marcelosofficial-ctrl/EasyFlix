using EasyFlix.Core.Services;

namespace EasyFlix.App.Services;

public static class LanguageDisplay
{
    public static string Name(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return "Unknown";
        return language.Trim().ToLowerInvariant() switch
        {
            "en" or "eng" => "English",
            "ja" or "jpn" => "Japanese",
            "pt" or "por" => "Portuguese",
            "es" or "spa" => "Spanish",
            "fr" or "fra" or "fre" => "French",
            "de" or "deu" or "ger" => "German",
            "it" or "ita" => "Italian",
            "ko" or "kor" => "Korean",
            "zh" or "zho" or "chi" => "Chinese",
            _ => language.ToUpperInvariant()
        };
    }

    public static string Audio(AudioStreamInfo stream)
        => $"Track {stream.Index}: {Name(stream.Language)}";

    public static string Subtitle(CatalogSubtitleStreamInfo track)
    {
        var suffix = string.Equals(track.Kind, "embedded", StringComparison.OrdinalIgnoreCase)
            ? "embedded"
            : "sidecar";
        return $"{Name(track.Language)} ({suffix})";
    }
}
