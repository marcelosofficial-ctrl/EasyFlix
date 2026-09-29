using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.App.Services;

public sealed class ArtworkCacheService
{
    private readonly HttpClient _http = new();
    private readonly SecretStore _secrets = new();

    public async Task SearchAndCacheAsync(MediaItem item, string libraryPath, CancellationToken cancellationToken = default)
    {
        var artworkFolder = Path.Combine(libraryPath, "_Artwork");
        var safeName = string.Concat(item.SuggestedName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var iconPath = Path.Combine(artworkFolder, safeName + ".ico");
        if (File.Exists(iconPath))
        {
            item.ArtworkPath = iconPath;
            item.ArtworkStatus = "Cached";
            return;
        }
        IArtworkProvider provider = item.Kind == MediaKind.Movie
            ? new TmdbArtworkProvider(_http, _secrets.Get(SecretStore.TmdbApiKey) ?? Environment.GetEnvironmentVariable("TMDB_API_KEY") ?? string.Empty)
            : new TvMazeArtworkProvider(_http);
        var match = await provider.FindAsync(item, cancellationToken);
        if (match is null)
        {
            item.ArtworkStatus = item.Kind == MediaKind.Movie && !_secrets.Has(SecretStore.TmdbApiKey) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TMDB_API_KEY"))
                ? "TMDB key required"
                : "No confident match";
            return;
        }

        using var response = await _http.GetAsync(match.ImageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 10_000_000)
            throw new InvalidDataException("Artwork response size was outside the safe range.");
        await using var inputStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await inputStream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (limited.Length + read > 10_000_000)
                throw new InvalidDataException("Artwork response size was outside the safe range.");
            limited.Write(buffer, 0, read);
        }
        var bytes = limited.ToArray();
        if (bytes.Length < 1_000) throw new InvalidDataException("Artwork response size was outside the safe range.");
        Directory.CreateDirectory(artworkFolder);
        using var input = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        SaveIco(decoder.Frames[0], iconPath);
        item.ArtworkPath = iconPath;
        item.ArtworkStatus = $"{match.Provider} ┬╖ {match.Confidence:P0}";
    }

    private static void SaveIco(BitmapSource source, string destination)
    {
        const int size = 256;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 24, 33)), null, new Rect(0, 0, size, size));
            var scale = Math.Min((double)size / source.PixelWidth, (double)size / source.PixelHeight);
            var width = source.PixelWidth * scale;
            var height = source.PixelHeight * scale;
            context.DrawImage(source, new Rect((size - width) / 2, (size - height) / 2, width, height));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var pngStream = new MemoryStream();
        png.Save(pngStream);
        var bytes = pngStream.ToArray();

        using var output = File.Create(destination);
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(bytes.Length);
        writer.Write(22);
        writer.Write(bytes);
    }
}
