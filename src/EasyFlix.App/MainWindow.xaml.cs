using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using EasyFlix.App.ViewModels;
using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.ScanCommand.Execute(null);
    }

    private void ProviderSettings_Click(object sender, RoutedEventArgs e)
        => new ProviderSettingsWindow { Owner = this }.ShowDialog();

    private void Activity_Click(object sender, RoutedEventArgs e)
        => new ActivityWindow { Owner = this }.ShowDialog();

    private void AddMediaFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder("Choose a media folder or drive") is not { } path || DataContext is not MainViewModel viewModel) return;
        viewModel.SourceRoots = AppendUniquePath(viewModel.SourceRoots, path);
    }

    private void AddExcludedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder("Choose a folder EasyFlix must never scan") is not { } path || DataContext is not MainViewModel viewModel) return;
        viewModel.ExcludedPaths = AppendUniquePath(viewModel.ExcludedPaths, path);
    }

    private void ChooseLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder("Choose the unified shortcut library") is not { } path || DataContext is not MainViewModel viewModel) return;
        viewModel.LibraryPath = path;
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !Directory.Exists(viewModel.LibraryPath))
        {
            MessageBox.Show("Create the library shortcuts first, or choose an existing library folder.", "EasyFlix", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", viewModel.LibraryPath) { UseShellExecute = true });
    }

    private void PreviewGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PreviewGrid.SelectedItem is not MediaItem item || (!File.Exists(item.SourcePath) && !Directory.Exists(item.SourcePath))) return;
        var start = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
        if (item.IsFolder) start.ArgumentList.Add(item.SourcePath);
        else
        {
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(item.SourcePath);
        }
        Process.Start(start);
    }

    private static string? ChooseFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog() == true ? PathSafety.Normalize(dialog.FolderName) : null;
    }

    private static string AppendUniquePath(string current, string path)
    {
        var values = current.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(PathSafety.Normalize)
            .Append(PathSafety.Normalize(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(Environment.NewLine, values);
    }
}
