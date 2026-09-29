using System.Globalization;
using System.Text;

namespace EasyFlix.Core.Services;

public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

public static class SrtWriter
{
    public static byte[] Encode(IEnumerable<SubtitleCue> cues)
    {
        var output = new StringBuilder();
        var index = 0;
        foreach (var cue in cues)
        {
            if (cue.Start < TimeSpan.Zero || cue.End <= cue.Start || string.IsNullOrWhiteSpace(cue.Text)) continue;
            var text = string.Join(Environment.NewLine,
                cue.Text.Replace("\0", string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (string.IsNullOrWhiteSpace(text)) continue;
            output.AppendLine((++index).ToString(CultureInfo.InvariantCulture));
            output.Append(FormatTime(cue.Start)).Append(" --> ").AppendLine(FormatTime(cue.End));
            output.AppendLine(text).AppendLine();
        }
        return new UTF8Encoding(false).GetBytes(output.ToString());
    }

    private static string FormatTime(TimeSpan value)
        => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
}
