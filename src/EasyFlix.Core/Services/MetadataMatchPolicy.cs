using System.Globalization;
using System.Text;

namespace EasyFlix.Core.Services;

public static class MetadataMatchPolicy
{
    public const double MinimumConfidence = .80;

    private static readonly HashSet<string> LowInformationTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "anime", "bonus", "bonus features", "drama", "episode", "episodes",
        "extra", "extras", "featurette", "featurettes", "film", "films",
        "library", "media", "movie", "movies", "sample", "samples", "season",
        "seasons", "show", "shows", "special", "specials", "trailer", "trailers",
        "tv", "tv show", "tv shows", "video", "videos"
    };

    public static double Score(
        string wanted,
        string candidate,
        string? wantedYear = null,
        string? candidateYear = null)
    {
        var wantedNormalized = Normalize(wanted);
        var candidateNormalized = Normalize(candidate);
        if (wantedNormalized.Length == 0 || candidateNormalized.Length == 0)
            return 0;

        var wantedTokens = Tokens(wantedNormalized);
        var candidateTokens = Tokens(candidateNormalized);
        if (wantedTokens.Count == 0 || candidateTokens.Count == 0)
            return 0;

        var shared = wantedTokens.Intersect(candidateTokens).Count();
        var precision = (double)shared / candidateTokens.Count;
        var recall = (double)shared / wantedTokens.Count;
        var score = precision + recall > 0
            ? 2 * precision * recall / (precision + recall)
            : 0;

        if (wantedNormalized == candidateNormalized)
            score = .95;

        if (!string.IsNullOrWhiteSpace(wantedYear)
            && !string.IsNullOrWhiteSpace(candidateYear))
        {
            score += string.Equals(wantedYear, candidateYear, StringComparison.OrdinalIgnoreCase)
                ? .05
                : -.10;
        }

        return Math.Clamp(score, 0, 1);
    }

    public static bool IsAcceptable(
        string wanted,
        string candidate,
        string? wantedYear = null,
        string? candidateYear = null)
    {
        var wantedNormalized = Normalize(wanted);
        var candidateNormalized = Normalize(candidate);
        if (wantedNormalized.Length == 0 || candidateNormalized.Length == 0)
            return false;
        if (LowInformationTitles.Contains(wantedNormalized))
            return false;

        var wantedTokens = Tokens(wantedNormalized);
        var candidateTokens = Tokens(candidateNormalized);
        if (wantedTokens.Count == 0 || candidateTokens.Count == 0)
            return false;

        if (HasNumericIdentityMismatch(wantedTokens, candidateTokens))
            return false;

        if (wantedTokens.Count <= 3
            && wantedNormalized != candidateNormalized
            && !wantedTokens.SetEquals(candidateTokens))
            return false;

        return Score(wanted, candidate, wantedYear, candidateYear) >= MinimumConfidence;
    }

    private static bool HasNumericIdentityMismatch(
        HashSet<string> wantedTokens,
        HashSet<string> candidateTokens)
    {
        var wantedNumbers = wantedTokens.Where(IsNumericToken)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateNumbers = candidateTokens.Where(IsNumericToken)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (wantedNumbers.Count > 0 || candidateNumbers.Count > 0)
            && !wantedNumbers.SetEquals(candidateNumbers);
    }

    private static bool IsNumericToken(string token)
        => token.Length > 0 && token.All(char.IsDigit);

    private static HashSet<string> Tokens(string normalized)
        => normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token is not ("the" or "a" or "an" or "and"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var chars = decomposed
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ')
            .ToArray();

        return string.Join(' ', new string(chars)
            .Normalize(NormalizationForm.FormC)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
