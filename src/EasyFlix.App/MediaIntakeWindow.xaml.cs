using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EasyFlix.App.Services;
using EasyFlix.App.ViewModels;
using EasyFlix.Core.Services;
using Microsoft.Win32;

namespace EasyFlix.App;

public partial class MediaIntakeWindow : Window
{
    private bool _importBusy;
    private readonly HandoffFileAssociationService _handoffAssociation = new();

    public IReadOnlyList<MediaGuideSection> GuideSections => MediaIntakeGuide.Sections;

    public MediaIntakeWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fade);
        RefreshHandoffAssociationStatus();
    }

    private async void ImportHandoff_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import EasyFlix media handoff",
            DefaultExt = ".easyflixhandoff",
            Filter = "EasyFlix media handoff (*.easyflixhandoff;*.txt;*.json)|*.easyflixhandoff;*.txt;*.json|EasyFlix handoff (*.easyflixhandoff)|*.easyflixhandoff|Text handoff (*.txt)|*.txt|JSON manifest (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
            await ProcessHandoffAsync(dialog.FileName);
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (paths is not { Length: 1 })
        {
            SetStatus("Drop exactly one EasyFlix .easyflixhandoff, .txt, or .json handoff.", isError: true);
            return;
        }

        await ProcessHandoffAsync(paths[0]);
    }

    private async Task ProcessHandoffAsync(string sourcePath)
    {
        if (_importBusy)
        {
            SetStatus("A media handoff import is already running.");
            return;
        }

        _importBusy = true;
        try
        {
            if (Owner?.DataContext is not BrowseViewModel browseViewModel)
                throw new InvalidOperationException("Open Add Media Center from the EasyFlix browser before importing.");

            var execution = new MediaIntakeExecutionService(browseViewModel.Store);
            var preview = execution.Preview(sourcePath);

            if (!preview.CanImport)
            {
                var errors = string.Join(
                    Environment.NewLine,
                    preview.Errors.Take(10).Select(value => "- " + value));
                MessageBox.Show(
                    this,
                    $"EasyFlix found {preview.Errors.Count} blocking issue(s):{Environment.NewLine}{Environment.NewLine}{errors}",
                    "Media handoff needs attention",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                SetStatus($"Handoff validation failed with {preview.Errors.Count} issue(s).", isError: true);
                return;
            }

            var warningText = preview.Warnings.Count == 0
                ? "No parser warnings."
                : string.Join(Environment.NewLine, preview.Warnings.Take(6).Select(value => "- " + value));
            var confirmation = MessageBox.Show(
                this,
                $"{preview.Format}: {preview.Manifest.Entries.Count} source entr{(preview.Manifest.Entries.Count == 1 ? "y" : "ies")} / " +
                $"{preview.LogicalTitles} logical title(s).{Environment.NewLine}" +
                $"{preview.NewEntries} new source entr{(preview.NewEntries == 1 ? "y" : "ies")}, {preview.Updates} update(s).{Environment.NewLine}" +
                $"All supplied media/artwork paths passed validation.{Environment.NewLine}" +
                $"Provider sync will NOT run.{Environment.NewLine}{Environment.NewLine}" +
                $"{warningText}{Environment.NewLine}{Environment.NewLine}" +
                "EasyFlix will back up the catalog, Refresh, import the handoff, Refresh again, and verify every requested entry. Continue?",
                "Import media handoff",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmation != MessageBoxResult.Yes)
            {
                SetStatus("Media handoff import cancelled.");
                return;
            }

            var progress = new Progress<string>(message => SetStatus(message));
            var outcome = await execution.ExecuteAsync(sourcePath, progress);
            await browseViewModel.ReloadAfterExternalImportAsync();

            var command = outcome.Command;
            var verification = outcome.VerificationReport;
            var resultLines = verification?.Results
                .Take(20)
                .Select(item => $"{(item.Applied ? "PASS" : "UNRESOLVED")}  {item.Title}")
                .ToArray() ?? [];

            var verified = verification?.Summary.Applied ?? 0;
            var entries = verification?.Summary.Entries ?? preview.Manifest.Entries.Count;
            var resultText =
                $"New entries: {command.Imported}. Updates: {command.Updated}. " +
                $"Final verification: {verified}/{entries} resolved, {command.Unresolved} unresolved." +
                Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, resultLines);

            if (verification is not null && verification.Results.Count > resultLines.Length)
                resultText += $"{Environment.NewLine}...and {verification.Results.Count - resultLines.Length} more.";

            resultText +=
                $"{Environment.NewLine}{Environment.NewLine}Backup: {outcome.BackupDirectory ?? "<none>"}" +
                $"{Environment.NewLine}Report: {command.ReportPath ?? "<none>"}" +
                $"{Environment.NewLine}Exit code: {MediaImportCommandContract.GetExitCode(command)}";

            MessageBox.Show(
                this,
                resultText,
                command.Success ? "EasyFlix media import complete" : "EasyFlix media import finished with unresolved items",
                MessageBoxButton.OK,
                command.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

            SetStatus(
                command.Success
                    ? $"Import complete: {verified}/{entries} verified. No provider sync was used."
                    : $"Import failed at {command.Stage} with {command.Unresolved} unresolved item(s). See the saved report.",
                isError: !command.Success);
        }
        catch (Exception ex)
        {
            SetStatus("Could not import the media handoff: " + ex.Message, isError: true);
            MessageBox.Show(
                this,
                ex.Message,
                "EasyFlix media import failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _importBusy = false;
        }
    }

    private void CopyAi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(MediaIntakeGuide.BuildAiHandoff());
            SetStatus("AI instructions copied. Paste them into any normal AI chat.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not copy the handoff: " + ex.Message, isError: true);
        }
    }

    private void SaveAi_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save EasyFlix AI media-intake handoff",
            FileName = "EasyFlix_Media_Intake_AI_Handoff.txt",
            DefaultExt = ".txt",
            Filter = "Text document (*.txt)|*.txt|All files (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(
                dialog.FileName,
                MediaIntakeGuide.BuildAiHandoff(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            SetStatus("AI handoff saved. It explains the direct .txt/.json import format.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not save the handoff: " + ex.Message, isError: true);
        }
    }

    private void HandoffAssociation_Click(object sender, RoutedEventArgs e)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            SetStatus("Could not locate the current EasyFlix executable.", isError: true);
            return;
        }

        try
        {
            if (_handoffAssociation.IsRegistered(executablePath))
            {
                _handoffAssociation.Unregister();
                SetStatus(".easyflixhandoff double-click import disabled.");
            }
            else
            {
                _handoffAssociation.Register(executablePath);
                SetStatus(".easyflixhandoff files now open with this EasyFlix installation.");
                MessageBox.Show(
                    this,
                    ".easyflixhandoff double-click is enabled for this Windows user.",
                    "EasyFlix handoff integration enabled",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            RefreshHandoffAssociationStatus();
        }
        catch (Exception ex)
        {
            var message = "Could not update .easyflixhandoff integration: " + ex.Message;
            SetStatus(message, isError: true);
            MessageBox.Show(
                this,
                message,
                "EasyFlix handoff integration failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void RefreshHandoffAssociationStatus()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            HandoffAssociationButton.IsEnabled = false;
            HandoffAssociationStatus.Text = "Executable path unavailable.";
            return;
        }

        var registered = _handoffAssociation.IsRegistered(executablePath);
        HandoffAssociationButton.Content = registered
            ? "Disable .easyflixhandoff double-click"
            : "Enable .easyflixhandoff double-click";
        HandoffAssociationStatus.Text = registered
            ? "Enabled for this Windows user."
            : "Optional. Uses only the dedicated .easyflixhandoff extension.";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

    private void SetStatus(string message, bool isError = false)
    {
        ActionStatus.Text = message;
        ActionStatus.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(255, 142, 142))
            : new SolidColorBrush(Color.FromRgb(117, 230, 247));
    }
}
