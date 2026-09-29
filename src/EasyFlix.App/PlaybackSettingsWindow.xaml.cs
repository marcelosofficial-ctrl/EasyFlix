using System.IO;
using System.Windows;
using EasyFlix.App.Services;
using Microsoft.Win32;

namespace EasyFlix.App;

public partial class PlaybackSettingsWindow : Window
{
    private readonly PlaybackSettingsService _settings = new();

    public PlaybackSettingsWindow()
    {
        InitializeComponent();

        var configured = _settings.Load();
        MpvPath.Text = string.IsNullOrWhiteSpace(configured.MpvPath)
            ? PlaybackSettingsService.FindMpvExecutable() ?? string.Empty
            : configured.MpvPath;
        VlcPath.Text = string.IsNullOrWhiteSpace(configured.VlcPath)
            ? PlaybackSettingsService.FindVlcExecutable() ?? string.Empty
            : configured.VlcPath;

        RefreshStatus();
    }

    private void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        var detected = PlaybackSettingsService.FindMpvExecutable();
        if (detected is null)
        {
            MpvStatus.Text = "mpv was not found automatically. Use Browse to select mpv.exe manually.";
            return;
        }

        MpvPath.Text = detected;
        RefreshStatus();
    }

    private void AutoDetectVlc_Click(object sender, RoutedEventArgs e)
    {
        var detected = PlaybackSettingsService.FindVlcExecutable();
        if (detected is null)
        {
            VlcStatus.Text = "VLC was not found automatically. Use Browse to select vlc.exe manually.";
            return;
        }

        VlcPath.Text = detected;
        RefreshStatus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose mpv.exe",
            Filter = "mpv executable (mpv.exe)|mpv.exe|Executable files (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            MpvPath.Text = dialog.FileName;
            RefreshStatus();
        }
    }

    private void BrowseVlc_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose vlc.exe",
            Filter = "VLC executable (vlc.exe)|vlc.exe|Executable files (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            VlcPath.Text = dialog.FileName;
            RefreshStatus();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var mpvPath = string.IsNullOrWhiteSpace(MpvPath.Text) ? null : MpvPath.Text.Trim();
        var vlcPath = string.IsNullOrWhiteSpace(VlcPath.Text) ? null : VlcPath.Text.Trim();
        if (mpvPath is not null && !File.Exists(mpvPath))
        {
            MpvStatus.Text = "Cannot save: the selected mpv executable does not exist.";
            return;
        }
        if (vlcPath is not null && !File.Exists(vlcPath))
        {
            VlcStatus.Text = "Cannot save: the selected VLC executable does not exist.";
            return;
        }

        _settings.Save(new PlaybackSettings(mpvPath, vlcPath));
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void RefreshStatus()
    {
        var mpvPath = MpvPath.Text?.Trim();
        MpvStatus.Text = !string.IsNullOrWhiteSpace(mpvPath) && File.Exists(mpvPath)
            ? "READY: mpv is available for verified tracks, resume, and progress."
            : string.IsNullOrWhiteSpace(mpvPath)
                ? "mpv is not configured."
                : "The selected mpv executable was not found.";

        var vlcPath = VlcPath.Text?.Trim();
        VlcStatus.Text = !string.IsNullOrWhiteSpace(vlcPath) && File.Exists(vlcPath)
            ? "READY: VLC is available for tracked resume, progress, history, completion, and Up Next."
            : string.IsNullOrWhiteSpace(vlcPath)
                ? "VLC is not configured. EasyFlix will auto-detect it when possible."
                : "The selected VLC executable was not found.";
    }
}
