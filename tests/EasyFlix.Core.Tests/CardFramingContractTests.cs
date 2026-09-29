namespace EasyFlix.Core.Tests;

public sealed class CardFramingContractTests
{
    [Fact]
    public void MainLibraryCardFraming_StaysCenteredUniformToFillWithoutPerTitleOffsets()
    {
        var root = FindRepositoryRoot();
        var converterPath = Path.Combine(root, "src", "EasyFlix.App", "ArtworkStretchConverter.cs");
        var browsePath = Path.Combine(root, "src", "EasyFlix.App", "BrowseWindow.xaml");

        var converter = File.ReadAllText(converterPath);
        var browse = File.ReadAllText(browsePath);

        Assert.Contains("? Stretch.Uniform", converter);
        Assert.Contains(": Stretch.UniformToFill;", converter);
        Assert.Contains("Product invariant: card artwork is center-cropped in one fixed viewport at every window size.", browse);

        const string binding = "Stretch=\"{Binding ArtworkPath, Converter={StaticResource ArtworkStretchConverter}}\"";
        Assert.True(CountOccurrences(browse, binding) >= 2, "Expected the main card templates to keep using the shared artwork stretch converter.");

        Assert.DoesNotContain("ArtworkOffset", browse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PosterOffset", browse, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EasyFlix.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("EasyFlix repository root not found.");
    }
}
