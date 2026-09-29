namespace EasyFlix.Core.Tests;

public sealed class OfflineOpenLocationFeedbackContractTests
{
    [Fact]
    public void OpenLocationExplainsUnavailableSelectedFile()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "src", "EasyFlix.App", "TitleDetailsWindow.xaml.cs");
        var source = File.ReadAllText(path);
        var start = source.IndexOf("private void OpenLocation_Click", StringComparison.Ordinal);

        Assert.True(start >= 0);
        var handler = source[start..];
        Assert.Contains("if (file is null)", handler);
        Assert.Contains("Select a file or episode first.", handler);
        Assert.Contains("if (!File.Exists(file.Path))", handler);
        Assert.Contains("This file is currently unavailable.", handler);
        Assert.Contains("Reconnect its drive or restore the file", handler);
        Assert.Contains("ProcessStartInfo(\"explorer.exe\")", handler);
        Assert.True(
            handler.IndexOf("if (!File.Exists(file.Path))", StringComparison.Ordinal)
            < handler.IndexOf("ProcessStartInfo(\"explorer.exe\")", StringComparison.Ordinal));
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

