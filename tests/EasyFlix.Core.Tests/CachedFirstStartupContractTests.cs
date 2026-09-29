namespace EasyFlix.Core.Tests;

public sealed class CachedFirstStartupContractTests
{
    [Fact]
    public void Startup_ShowsCachedLibraryBeforePersonalizationAndFilesystemReconcile()
    {
        var root = FindRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "src", "EasyFlix.App", "ViewModels", "BrowseViewModel.cs"));
        var window = File.ReadAllText(Path.Combine(root, "src", "EasyFlix.App", "BrowseWindow.xaml.cs"));

        Assert.Contains("ReloadCatalogViewAsync(_lifetime.Token, includePersonalized: false)", viewModel);
        Assert.Contains("public async Task HydratePersonalizedStartupAsync()", viewModel);
        Assert.Contains("if (includePersonalized)", viewModel);
        Assert.Contains("RebuildHeroCandidates(new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase))", viewModel);

        var fadeIndex = window.IndexOf("StartupOverlay.BeginAnimation(OpacityProperty, fade);", StringComparison.Ordinal);
        var hydrateIndex = window.IndexOf("await _viewModel.HydratePersonalizedStartupAsync();", StringComparison.Ordinal);
        var reconcileIndex = window.IndexOf("await _viewModel.ReconcileAfterStartupAsync();", StringComparison.Ordinal);

        Assert.True(fadeIndex >= 0);
        Assert.True(hydrateIndex > fadeIndex);
        Assert.True(reconcileIndex > hydrateIndex);
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