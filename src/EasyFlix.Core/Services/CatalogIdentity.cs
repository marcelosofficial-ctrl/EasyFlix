using System.Security.Cryptography;
using System.Text;
using EasyFlix.Core.Models;

namespace EasyFlix.Core.Services;

public static class CatalogIdentity
{
    public static string TitleId(MediaKind kind, string displayTitle, string? year)
        => Hash($"title|{(int)kind}|{Normalize(displayTitle)}|{year?.Trim() ?? string.Empty}");

    public static string SeriesTitleId(string displayTitle)
        => Hash($"series|{Normalize(displayTitle)}");

    public static string SeasonId(string titleId, int? seasonNumber, string label)
        => Hash($"season|{titleId}|{seasonNumber?.ToString() ?? "?"}|{Normalize(label)}");

    public static string EpisodeId(string seasonId, int? episodeNumber, string fileName)
        => Hash($"episode|{seasonId}|{episodeNumber?.ToString() ?? "?"}|{Normalize(fileName)}");

    public static string FileId(string path)
        => Hash($"file|{PathSafety.Normalize(path).ToLowerInvariant()}");

    private static string Hash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private static string Normalize(string value)
        => string.Join(' ', value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
