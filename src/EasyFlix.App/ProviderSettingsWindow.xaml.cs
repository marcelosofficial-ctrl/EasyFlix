using System.Diagnostics;
using System.Windows;
using EasyFlix.App.Services;
using EasyFlix.Core.Services;

namespace EasyFlix.App;

public partial class ProviderSettingsWindow : Window
{
    private readonly SecretStore _store = new();

    public ProviderSettingsWindow()
    {
        InitializeComponent();
        RefreshStatus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TmdbKey.Password)) _store.Set(SecretStore.TmdbApiKey, TmdbKey.Password);
        if (!string.IsNullOrWhiteSpace(SubDlKey.Password)) _store.Set(SecretStore.SubDlApiKey, SubDlKey.Password);
        if (!string.IsNullOrWhiteSpace(OpenSubtitlesUsername.Password)) _store.Set(SecretStore.OpenSubtitlesUsername, OpenSubtitlesUsername.Password);
        if (!string.IsNullOrWhiteSpace(OpenSubtitlesPassword.Password)) _store.Set(SecretStore.OpenSubtitlesPassword, OpenSubtitlesPassword.Password);
        if (!string.IsNullOrWhiteSpace(OpenSubtitlesApiKey.Password)) _store.Set(SecretStore.OpenSubtitlesApiKey, OpenSubtitlesApiKey.Password);
        TmdbKey.Clear();
        SubDlKey.Clear();
        OpenSubtitlesUsername.Clear();
        OpenSubtitlesPassword.Clear();
        OpenSubtitlesApiKey.Clear();
        DialogResult = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Remove all encrypted provider credentials?", "EasyFlix", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _store.Set(SecretStore.TmdbApiKey, null);
        _store.Set(SecretStore.SubDlApiKey, null);
        _store.Set(SecretStore.OpenSubtitlesUsername, null);
        _store.Set(SecretStore.OpenSubtitlesPassword, null);
        _store.Set(SecretStore.OpenSubtitlesApiKey, null);
        RefreshStatus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FfprobeStatus.Text = await MediaProbeService.IsAvailableAsync() ? "READY" : "NOT FOUND";
        FfprobeStatus.Foreground = FfprobeStatus.Text == "READY"
            ? System.Windows.Media.Brushes.MediumSpringGreen
            : System.Windows.Media.Brushes.Orange;
        WhisperStatus.Text = LocalSubtitleService.IsModelReady ? "MODEL READY" : "MODEL DOWNLOAD NEEDED";
        WhisperStatus.Foreground = LocalSubtitleService.IsModelReady
            ? System.Windows.Media.Brushes.MediumSpringGreen
            : System.Windows.Media.Brushes.Gold;
        FfmpegStatus.Text = await LocalSubtitleService.IsFfmpegAvailableAsync() ? "FFMPEG: READY" : "FFMPEG: NOT FOUND";
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string url }) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Could not open provider page", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void RefreshStatus()
    {
        TmdbStatus.Text = _store.Has(SecretStore.TmdbApiKey) ? "Configured securely" : "Optional key not configured";
        SubDlStatus.Text = _store.Has(SecretStore.SubDlApiKey) ? "Configured securely" : "Key required for downloads";
        OpenSubtitlesStatus.Text = _store.Has(SecretStore.OpenSubtitlesApiKey) &&
                                   _store.Has(SecretStore.OpenSubtitlesUsername) &&
                                   _store.Has(SecretStore.OpenSubtitlesPassword)
            ? "Configured securely"
            : "Optional fallback not configured";
    }
}
