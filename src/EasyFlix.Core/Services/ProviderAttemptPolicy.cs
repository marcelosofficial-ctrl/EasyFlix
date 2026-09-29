namespace EasyFlix.Core.Services;

public static class ProviderAttemptPolicy
{
    public static bool ShouldDefer(
        string status,
        string detail,
        string provider,
        DateTimeOffset timestamp,
        DateTimeOffset now,
        TimeSpan retryAfter)
    {
        if (retryAfter < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryAfter));
        if (string.IsNullOrWhiteSpace(provider)) return false;
        if (!status.Equals("NotFound", StringComparison.OrdinalIgnoreCase) &&
            !status.Equals("UnsafeCandidate", StringComparison.OrdinalIgnoreCase)) return false;
        return timestamp >= now - retryAfter &&
               detail.StartsWith(provider + " ┬╖", StringComparison.OrdinalIgnoreCase);
    }
}
