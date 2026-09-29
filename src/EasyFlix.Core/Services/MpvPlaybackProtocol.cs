using System.Text.Json;

namespace EasyFlix.Core.Services;

public sealed record MpvTrackInfo(
    long Id,
    string Type,
    int? FfIndex,
    bool External,
    string? ExternalFilename);

public sealed record MpvResolvedSelection(
    long? AudioTrackId,
    bool AudioResolved,
    long? SubtitleTrackId,
    bool SubtitleResolved,
    bool DisableSubtitles);

public static class MpvPlaybackProtocol
{
    public static IReadOnlyList<MpvTrackInfo> ParseTrackList(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array) return [];

        var tracks = new List<MpvTrackInfo>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idValue) || !idValue.TryGetInt64(out var id))
                continue;
            if (!item.TryGetProperty("type", out var typeValue))
                continue;

            var type = typeValue.GetString();
            if (string.IsNullOrWhiteSpace(type)) continue;

            int? ffIndex = null;
            if (item.TryGetProperty("ff-index", out var ffValue) &&
                ffValue.ValueKind == JsonValueKind.Number &&
                ffValue.TryGetInt32(out var parsedIndex))
                ffIndex = parsedIndex;

            var external = item.TryGetProperty("external", out var externalValue) &&
                           externalValue.ValueKind == JsonValueKind.True;

            string? externalFilename = null;
            if (item.TryGetProperty("external-filename", out var filenameValue) &&
                filenameValue.ValueKind == JsonValueKind.String)
                externalFilename = filenameValue.GetString();

            tracks.Add(new MpvTrackInfo(id, type, ffIndex, external, externalFilename));
        }

        return tracks;
    }

    public static bool IsNaturalEndFile(JsonElement eventObject)
        => eventObject.ValueKind == JsonValueKind.Object &&
           eventObject.TryGetProperty("event", out var eventValue) &&
           string.Equals(eventValue.GetString(), "end-file", StringComparison.Ordinal) &&
           eventObject.TryGetProperty("reason", out var reasonValue) &&
           string.Equals(reasonValue.GetString(), "eof", StringComparison.Ordinal);

    public static MpvResolvedSelection ResolveSelection(
        IReadOnlyList<MpvTrackInfo> tracks,
        int? audioStreamIndex,
        CatalogSubtitleStreamInfo? subtitleTrack,
        bool subtitleSelectionSpecified)
    {
        var audio = ResolveUnique(
            tracks,
            track => track.Type.Equals("audio", StringComparison.OrdinalIgnoreCase) &&
                     !track.External &&
                     audioStreamIndex is not null &&
                     track.FfIndex == audioStreamIndex);

        if (!subtitleSelectionSpecified)
            return new MpvResolvedSelection(audio, audio is not null, null, false, false);

        if (subtitleTrack is null)
            return new MpvResolvedSelection(audio, audio is not null, null, true, true);

        long? subtitle;
        if (!string.IsNullOrWhiteSpace(subtitleTrack.SourcePath))
        {
            subtitle = ResolveUnique(
                tracks,
                track => track.Type.Equals("sub", StringComparison.OrdinalIgnoreCase) &&
                         track.External &&
                         PathsEqual(track.ExternalFilename, subtitleTrack.SourcePath));
        }
        else if (subtitleTrack.StreamIndex is not null)
        {
            subtitle = ResolveUnique(
                tracks,
                track => track.Type.Equals("sub", StringComparison.OrdinalIgnoreCase) &&
                         !track.External &&
                         track.FfIndex == subtitleTrack.StreamIndex);
        }
        else
        {
            subtitle = null;
        }

        return new MpvResolvedSelection(
            audio,
            audio is not null,
            subtitle,
            subtitle is not null,
            false);
    }

    private static long? ResolveUnique(
        IReadOnlyList<MpvTrackInfo> tracks,
        Func<MpvTrackInfo, bool> predicate)
    {
        long? found = null;
        foreach (var track in tracks)
        {
            if (!predicate(track)) continue;
            if (found is not null) return null;
            found = track.Id;
        }
        return found;
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
