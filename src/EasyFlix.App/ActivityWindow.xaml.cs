using System.Diagnostics;
using System.IO;
using System.Windows;
using EasyFlix.App.Services;

namespace EasyFlix.App;

public partial class ActivityWindow : Window
{
    private readonly SubtitleJobStore _store = new();

    public ActivityWindow() => InitializeComponent();

    private void Window_Loaded(object sender, RoutedEventArgs e) => Refresh();
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Refresh()
    {
        var recent = _store.ReadRecent();
        ActivityGrid.ItemsSource = recent;
        FolderText.Text = _store.Folder;
        SummaryText.Text = recent.Count == 0
            ? "No ReelShelf subtitle events recorded yet."
            : $"Showing {recent.Count:N0} recent events ┬╖ {recent.Count(item => item.Status is "Installed" or "Generated"):N0} successful ┬╖ {recent.Count(item => item.Status == "Failed"):N0} failed";
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_store.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", _store.Folder) { UseShellExecute = true });
    }
}
