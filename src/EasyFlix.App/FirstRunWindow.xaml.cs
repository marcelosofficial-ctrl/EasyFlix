using System.IO;
using System.Windows;
using EasyFlix.App.Services;

namespace EasyFlix.App;

public partial class FirstRunWindow : Window
{
    private readonly AppSettingsService _appSettings = new();
    private readonly PlaybackSettingsService _playbackSettings = new();

    public FirstRunWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshReadiness();
    }

    private void ConfigureLibrary_Click(object sender, RoutedEventArgs e)
    {
        new LibraryMaintenanceWindow { Owner = this }.ShowDialog();
        RefreshReadiness();
    }

    private void ConfigurePlayback_Click(object sender, RoutedEventArgs e)
    {
        new PlaybackSettingsWindow { Owner = this }.ShowDialog();
        RefreshReadiness();
    }

    private void ConfigureProviders_Click(object sender, RoutedEventArgs e)
        => new ProviderSettingsWindow { Owner = this }.ShowDialog();

    private void Continue_Click(object sender, RoutedEventArgs e)
        => Close();

    private void RefreshReadiness()
    {
        var settings = _appSettings.Load();
        var rootCount = settings.SourceRoots
            .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;

        LibraryStatus.Text = rootCount > 0
            ? $"READY: {rootCount} media source root(s) configured."
            : "ACTION NEEDED: add at least one media source folder to populate the library.";

        var configuredMpv = _playbackSettings.Load().MpvPath;
        var mpv = !string.IsNullOrWhiteSpace(configuredMpv) && File.Exists(configuredMpv)
            ? configuredMpv
            : PlaybackSettingsService.FindMpvExecutable();

        PlaybackStatus.Text = mpv is not null
            ? "READY: mpv is available for verified track selection, resume, and progress."
            : "OPTIONAL: mpv is not configured. Windows default playback still works.";

        ReadinessSummary.Text = rootCount > 0
            ? "EasyFlix is ready. You can revisit this screen anytime from Setup and readiness."
            : "You can continue now, but the library stays empty until a media source is configured.";
    }
}
