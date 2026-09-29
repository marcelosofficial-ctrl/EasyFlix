using System.Text;
using System.Text.RegularExpressions;

namespace EasyFlix.Core.Services;

public sealed record SrtValidationResult(bool IsValid, int CueCount, TimeSpan LastCue, string Message);

public static partial class SrtValidator
{
    public const int MaximumBytes = 5_000_000;

    public static SrtValidationResult Validate(ReadOnlySpan<byte> payload, TimeSpan? mediaDuration = null)
    {
        if (payload.Length is < 20 or > MaximumBytes) return Invalid("Payload size is outside the safe SRT range.");
        if (payload.StartsWith("MZ"u8) || payload.StartsWith("PK"u8) || payload.StartsWith(new byte[] { 0x1F, 0x8B }))
            return Invalid("Executable or archive signature rejected.");
        if (payload.Contains((byte)0)) return Invalid("Binary payload rejected.");

        string text;
        try { text = new UTF8Encoding(false, true).GetString(payload); }
        catch (DecoderFallbackException) { return Invalid("Subtitle is not valid UTF-8 text."); }
        var matches = TimingLine().Matches(text);
        if (matches.Count == 0) return Invalid("No SRT timing cues were found.");
        var last = matches.Select(match => ParseTime(match.Groups[2].Value)).Max();
        if (mediaDuration is { } duration && last > duration + TimeSpan.FromMinutes(10))
            return Invalid("Subtitle timing extends far beyond the media duration.");
        return new SrtValidationResult(true, matches.Count, last, "Validated UTF-8 SRT.");
    }

    public static void InstallNew(string destination, ReadOnlySpan<byte> payload, TimeSpan? mediaDuration = null)
    {
        if (!Path.GetExtension(destination).Equals(".srt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Subtitle destination must end in .srt.");
        if (File.Exists(destination)) throw new IOException("Existing subtitles are never overwritten.");
        var result = Validate(payload, mediaDuration);
        if (!result.IsValid) throw new InvalidDataException(result.Message);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".reelshelf.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(payload);
            File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static SrtValidationResult Invalid(string message) => new(false, 0, TimeSpan.Zero, message);

    private static TimeSpan ParseTime(string value)
    {
        var parts = value.Replace('.', ',').Split([':', ',']);
        return new TimeSpan(0, int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
    }

    [GeneratedRegex(@"(?m)^\s*([0-9]{2}:[0-9]{2}:[0-9]{2}[,.][0-9]{3})\s+-->\s+([0-9]{2}:[0-9]{2}:[0-9]{2}[,.][0-9]{3})")]
    private static partial Regex TimingLine();
}
