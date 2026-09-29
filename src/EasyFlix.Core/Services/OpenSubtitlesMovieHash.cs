using System.Buffers.Binary;

namespace EasyFlix.Core.Services;

public static class OpenSubtitlesMovieHash
{
    private const int BlockSize = 64 * 1024;

    public static string Compute(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < BlockSize * 2)
            throw new InvalidDataException("File is too small for an OpenSubtitles movie hash.");

        var buffer = new byte[BlockSize];
        ulong hash = unchecked((ulong)stream.Length);
        hash = unchecked(hash + ReadBlock(stream, buffer, 0));
        hash = unchecked(hash + ReadBlock(stream, buffer, stream.Length - BlockSize));
        return hash.ToString("x16");
    }

    private static ulong ReadBlock(Stream stream, byte[] buffer, long position)
    {
        stream.Position = position;
        stream.ReadExactly(buffer);
        ulong sum = 0;
        for (var offset = 0; offset < buffer.Length; offset += sizeof(ulong))
            sum = unchecked(sum + BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, sizeof(ulong))));
        return sum;
    }
}
