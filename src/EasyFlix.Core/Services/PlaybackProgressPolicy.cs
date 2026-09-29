namespace EasyFlix.Core.Services;

public sealed record PlaybackProgressEntry(
    string TitleId,
    string MediaFileId,
    double PositionSeconds,
    double? DurationSeconds,
    DateTimeOffset UpdatedUtc,
    string? ContextLabel = null);

public sealed record UpNextEntry(
    string TitleId,
    string MediaFileId,
    string ContextLabel,
    DateTimeOffset SourceUpdatedUtc);

public static class PlaybackProgressPolicy
{
    public const double MinimumResumeSeconds = 60;
    public const double CompletionRatio = 0.95;
    public const double CompletionRemainingSeconds = 120;

    public static bool IsContinueWatching(double positionSeconds, double? durationSeconds)
    {
        if (!double.IsFinite(positionSeconds) || positionSeconds < MinimumResumeSeconds)
            return false;

        if (durationSeconds is null)
            return true;

        var duration = durationSeconds.Value;
        if (!double.IsFinite(duration) || duration <= 0 || positionSeconds >= duration)
            return false;

        var ratio = positionSeconds / duration;
        var remaining = duration - positionSeconds;
        return ratio < CompletionRatio && remaining > CompletionRemainingSeconds;
    }

    public static bool IsCompleted(double positionSeconds, double? durationSeconds)
    {
        if (!double.IsFinite(positionSeconds) || positionSeconds < 0 || durationSeconds is null)
            return false;

        var duration = durationSeconds.Value;
        if (!double.IsFinite(duration) || duration <= 0)
            return false;

        if (positionSeconds >= duration)
            return true;

        var ratio = positionSeconds / duration;
        var remaining = duration - positionSeconds;
        return ratio >= CompletionRatio || remaining <= CompletionRemainingSeconds;
    }

    public static IReadOnlyList<PlaybackProgressEntry> SelectContinueWatching(
        IEnumerable<PlaybackProgressEntry> newestFirst,
        int limit)
    {
        if (limit <= 0) return [];

        var result = new List<PlaybackProgressEntry>(limit);
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in newestFirst.OrderByDescending(value => value.UpdatedUtc))
        {
            if (!seenTitles.Add(entry.TitleId))
                continue;

            if (!IsContinueWatching(entry.PositionSeconds, entry.DurationSeconds))
                continue;

            result.Add(entry);
            if (result.Count == limit) break;
        }

        return result;
    }
}
