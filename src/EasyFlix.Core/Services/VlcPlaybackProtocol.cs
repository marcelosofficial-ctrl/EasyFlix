using System.Globalization;

namespace EasyFlix.Core.Services;

public static class VlcPlaybackProtocol
{
    public static bool TryParseSeconds(string? line, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var value = line.Trim().TrimStart('>').Trim();
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            !double.IsFinite(parsed) ||
            parsed < 0)
            return false;

        seconds = parsed;
        return true;
    }

    public static bool IsResumeApplied(double currentSeconds, double targetSeconds)
        => double.IsFinite(currentSeconds) &&
           double.IsFinite(targetSeconds) &&
           currentSeconds >= Math.Max(0, targetSeconds - 5) &&
           currentSeconds <= targetSeconds + 15;
}
