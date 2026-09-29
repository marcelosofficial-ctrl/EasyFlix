using System.Text;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class SrtValidatorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ReelShelfSrtTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AcceptsTimedUtf8Srt()
    {
        var payload = Encoding.UTF8.GetBytes("1\r\n00:00:01,000 --> 00:00:03,500\r\nHello.\r\n");

        var result = SrtValidator.Validate(payload, TimeSpan.FromMinutes(2));

        Assert.True(result.IsValid);
        Assert.Equal(1, result.CueCount);
    }

    [Theory]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 })]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 })]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08, 0x00 })]
    public void RejectsExecutableAndArchiveSignatures(byte[] payload)
        => Assert.False(SrtValidator.Validate(payload).IsValid);

    [Fact]
    public void InstallerNeverOverwritesExistingSubtitle()
    {
        Directory.CreateDirectory(_folder);
        var destination = Path.Combine(_folder, "Movie.en.srt");
        File.WriteAllText(destination, "existing");
        var payload = Encoding.UTF8.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nNew\n");

        Assert.Throws<IOException>(() => SrtValidator.InstallNew(destination, payload));
        Assert.Equal("existing", File.ReadAllText(destination));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
