using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace EasyFlix.App.Services;

public sealed class CatalogArtworkCacheService(HttpClient httpClient)
{
    private const int MaxArtworkBytes = 10_000_000;

    public Task<string?> CachePosterAsync(
        string titleId,
        Uri? imageUrl,
        CancellationToken cancellationToken = default)
        => CacheAsync(titleId, null, imageUrl, cancellationToken);

    public Task<string?> CacheBackdropAsync(
        string titleId,
        Uri? imageUrl,
        CancellationToken cancellationToken = default)
        => CacheAsync(titleId, "backdrop", imageUrl, cancellationToken);

    private async Task<string?> CacheAsync(
        string titleId,
        string? role,
        Uri? imageUrl,
        CancellationToken cancellationToken)
    {
        if (imageUrl is null) return null;

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyFlix",
            "artwork");
        var sourceHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(imageUrl.AbsoluteUri)))[..12];
        var roleSegment = string.IsNullOrWhiteSpace(role) ? string.Empty : role + "-";
        var destination = Path.Combine(folder, $"{titleId}-{roleSegment}{sourceHash}.png");
        if (File.Exists(destination)) return destination;

        using var response = await httpClient.GetAsync(
            imageUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxArtworkBytes)
            throw new InvalidDataException("Artwork response exceeded the EasyFlix size limit.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaxArtworkBytes)
                throw new InvalidDataException("Artwork response exceeded the EasyFlix size limit.");
            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length < 1_000)
            throw new InvalidDataException("Artwork response was too small to be a useful poster.");

        buffer.Position = 0;
        var decoder = BitmapDecoder.Create(
            buffer,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));

        Directory.CreateDirectory(folder);
        var temporary = destination + ".tmp";
        await using (var output = File.Create(temporary))
            encoder.Save(output);
        File.Move(temporary, destination, overwrite: true);
        return destination;
    }
}
