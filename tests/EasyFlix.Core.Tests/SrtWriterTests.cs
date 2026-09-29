using System.Text;
using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class SrtWriterTests
{
    [Fact]
    public void Encode_ProducesValidatedUtf8SrtAndSkipsInvalidCues()
    {
        var payload = SrtWriter.Encode([
            new(TimeSpan.FromSeconds(1.25), TimeSpan.FromSeconds(3.5), " Hello\0 \r\nworld "),
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4), "invalid")
        ]);

        var text = Encoding.UTF8.GetString(payload);
        Assert.Contains("00:00:01,250 --> 00:00:03,500", text);
        Assert.Contains($"Hello{Environment.NewLine}world", text);
        Assert.DoesNotContain("invalid", text);
        Assert.True(SrtValidator.Validate(payload).IsValid);
    }
}
