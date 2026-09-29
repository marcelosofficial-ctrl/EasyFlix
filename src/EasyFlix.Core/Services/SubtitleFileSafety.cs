namespace EasyFlix.Core.Services;

public static class SubtitleFileSafety
{
    public static string DestinationFor(string mediaPath, string language)
        => DestinationFor(mediaPath, language, generated: false);

    public static string DestinationForGenerated(string mediaPath, string language)
        => DestinationFor(mediaPath, language, generated: true);

    private static string DestinationFor(string mediaPath, string language, bool generated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        var normalizedLanguage = language.Trim().ToLowerInvariant() switch
        {
            "en" or "eng" or "english" => "en",
            "ja" or "jpn" or "japanese" => "ja",
            _ => throw new ArgumentOutOfRangeException(nameof(language), "Only English and Japanese destinations are supported.")
        };
        var directory = Path.GetDirectoryName(Path.GetFullPath(mediaPath))
            ?? throw new ArgumentException("The media path must include a directory.", nameof(mediaPath));
        var origin = generated ? ".generated" : string.Empty;
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(mediaPath)}.{normalizedLanguage}{origin}.srt");
    }
}
