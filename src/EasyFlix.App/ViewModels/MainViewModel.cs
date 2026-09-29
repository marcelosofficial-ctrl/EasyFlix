using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using EasyFlix.App.Infrastructure;
using EasyFlix.App.Services;
using EasyFlix.Core.Models;
using EasyFlix.Core.Services;

namespace EasyFlix.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly MediaScanner _scanner = new();
    private readonly LibraryPlanner _planner = new();
    private readonly ShortcutLibraryService _library = new();
    private readonly ArtworkCacheService _artwork = new();
    private readonly MediaProbeService _probe = new();
    private readonly SubDlSubtitleService _subDlSubtitles = new();
    private readonly OpenSubtitlesService _openSubtitles = new();
    private readonly LocalSubtitleService _localSubtitles = new();
    private readonly SecretStore _secrets = new();
    private readonly AppSettingsService _settings = new();
    private CancellationTokenSource? _operationCancellation;
    private string _sourceRoots = string.Empty;
    private string _excludedPaths = string.Empty;
    private string _libraryPath = string.Empty;
    private string _searchText = string.Empty;
    private string _status = "Ready for a safe, read-only scan.";
    private bool _isBusy;
    private int _movieCount;
    private int _tvCount;
    private int _animeCount;

    public MainViewModel()
    {
        var settings = _settings.Load();
        _sourceRoots = settings.SourceRoots;
        _excludedPaths = settings.ExcludedPaths;
        _libraryPath = settings.LibraryPath;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MediaItem.LibrarySection)));
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MediaItem.SeriesGroup)));
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MediaItem.SeasonGroup)));
        ItemsView.Filter = MatchesSearch;
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => !IsBusy && Items.Any(item => item.IsSelected));
        AuditCommand = new AsyncRelayCommand(AuditAsync, () => !IsBusy && Items.Any(item => item.IsSelected));
        ArtworkCommand = new AsyncRelayCommand(ArtworkAsync, () => !IsBusy && Items.Any(item => item.IsSelected));
        DownloadSubtitlesCommand = new AsyncRelayCommand(DownloadSubtitlesAsync, () => !IsBusy && Items.Any(item => item.IsSelected));
        GenerateLocalSubtitlesCommand = new AsyncRelayCommand(GenerateLocalSubtitlesAsync, () => !IsBusy && Items.Any(item => item.IsSelected));
        WorkflowCommand = new AsyncRelayCommand(ProcessSelectedAsync, () => !IsBusy && Items.Any(item => item.IsSelected));
        SelectAllCommand = new RelayCommand(() => SetSelection(true));
        SelectNoneCommand = new RelayCommand(() => SetSelection(false));
        CancelCommand = new RelayCommand(CancelOperation, () => IsBusy);
    }

    public ObservableCollection<MediaItem> Items { get; } = [];
    public ICollectionView ItemsView { get; }
    public Array MediaKinds => Enum.GetValues(typeof(MediaKind));
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand AuditCommand { get; }
    public AsyncRelayCommand ArtworkCommand { get; }
    public AsyncRelayCommand DownloadSubtitlesCommand { get; }
    public AsyncRelayCommand GenerateLocalSubtitlesCommand { get; }
    public AsyncRelayCommand WorkflowCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand CancelCommand { get; }

    public string SourceRoots { get => _sourceRoots; set => SetField(ref _sourceRoots, value); }
    public string ExcludedPaths { get => _excludedPaths; set => SetField(ref _excludedPaths, value); }
    public string LibraryPath { get => _libraryPath; set => SetField(ref _libraryPath, value); }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (string.Equals(_searchText, value, StringComparison.Ordinal)) return;
            _searchText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SearchText)));
            ItemsView.Refresh();
        }
    }
    public string Status { get => _status; set => SetField(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set { SetField(ref _isBusy, value); ScanCommand.RaiseCanExecuteChanged(); ApplyCommand.RaiseCanExecuteChanged(); AuditCommand.RaiseCanExecuteChanged(); ArtworkCommand.RaiseCanExecuteChanged(); DownloadSubtitlesCommand.RaiseCanExecuteChanged(); GenerateLocalSubtitlesCommand.RaiseCanExecuteChanged(); WorkflowCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); } }
    public int MovieCount { get => _movieCount; private set => SetField(ref _movieCount, value); }
    public int TvCount { get => _tvCount; private set => SetField(ref _tvCount, value); }
    public int AnimeCount { get => _animeCount; private set => SetField(ref _animeCount, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceRoots))
        {
            MessageBox.Show("Add at least one media folder or drive first.", "EasyFlix", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var cancellationToken = BeginOperation();
        try
        {
            var options = new ScanOptions(SplitPaths(SourceRoots), SplitPaths(ExcludedPaths), LibraryPath);
            _settings.Save(new AppSettings(SourceRoots, ExcludedPaths, LibraryPath));
            var progress = new Progress<string>(message => Status = message);
            var found = await _scanner.ScanAsync(options, progress, cancellationToken);
            Items.Clear();
            foreach (var item in found) Items.Add(item);
            RefreshCounts();
            Status = $"Preview ready: {Items.Count:N0} items found. Nothing has been changed.";
            ApplyCommand.RaiseCanExecuteChanged();
        }
        catch (OperationCanceledException) { Status = "Scan canceled safely. Nothing was changed."; }
        catch (Exception exception)
        {
            Status = "Scan stopped safely.";
            MessageBox.Show(exception.Message, "Scan error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndOperation(); }
    }

    private Task ApplyAsync()
    {
        if (!EnsureLibraryPath()) return Task.CompletedTask;
        var cancellationToken = BeginOperation();
        try
        {
            var plan = _planner.BuildPlan(Items, LibraryPath);
            var confirmation = MessageBox.Show(
                $"Create or refresh {plan.Count:N0} Windows shortcuts?\n\nStale targets and artwork will be repaired after media moves. Original media files will not be moved, renamed, or rewritten.",
                "Apply safe library plan", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes) return Task.CompletedTask;
            var summary = _library.Apply(plan, LibraryPath, cancellationToken);
            Status = $"Created {summary.Created:N0}, refreshed {summary.Updated:N0}, already current {summary.Skipped:N0}. Log saved.";
        }
        catch (OperationCanceledException) { Status = "Shortcut creation canceled safely. Completed shortcuts were kept."; }
        catch (Exception exception)
        {
            Status = "Apply stopped safely.";
            MessageBox.Show(exception.Message, "Library error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndOperation(); }
        return Task.CompletedTask;
    }

    private async Task AuditAsync()
    {
        var cancellationToken = BeginOperation();
        try
        {
            var selected = Items.Where(item => item.IsSelected).ToArray();
            for (var index = 0; index < selected.Length; index++)
            {
                var item = selected[index];
                Status = $"Auditing subtitles {index + 1:N0}/{selected.Length:N0}: {item.SuggestedName}";
                cancellationToken.ThrowIfCancellationRequested();
                var audit = await _probe.AuditAsync(item, cancellationToken: cancellationToken);
                item.SubtitleStatus = audit.Summary;
            }
            Status = $"Subtitle audit complete for {selected.Length:N0} items. No files were changed.";
        }
        catch (OperationCanceledException) { Status = "Subtitle audit canceled safely."; }
        catch (Exception exception)
        {
            Status = "Subtitle audit stopped safely.";
            MessageBox.Show($"Install FFmpeg/FFprobe or choose fewer items, then try again.\n\n{exception.Message}", "Audit error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndOperation(); }
    }

    private async Task ArtworkAsync()
    {
        var cancellationToken = BeginOperation();
        try
        {
            var selected = Items.Where(item => item.IsSelected).ToArray();
            for (var index = 0; index < selected.Length; index++)
            {
                var item = selected[index];
                Status = $"Matching artwork {index + 1:N0}/{selected.Length:N0}: {item.SuggestedName}";
                cancellationToken.ThrowIfCancellationRequested();
                await _artwork.SearchAndCacheAsync(item, LibraryPath, cancellationToken);
                await Task.Delay(200, cancellationToken);
            }
            Status = $"Artwork search complete for {selected.Length:N0} items. Review confidence before applying.";
        }
        catch (OperationCanceledException) { Status = "Artwork search canceled safely. Completed covers were kept."; }
        catch (Exception exception)
        {
            Status = "Artwork search stopped safely.";
            MessageBox.Show(exception.Message, "Artwork error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndOperation(); }
    }

    private async Task DownloadSubtitlesAsync()
    {
        var subDlKey = _secrets.Get(SecretStore.SubDlApiKey);
        var openCredentials = GetOpenSubtitlesCredentials();
        if (string.IsNullOrWhiteSpace(subDlKey) && openCredentials is null)
        {
            MessageBox.Show("Configure SubDL or OpenSubtitles first. Credentials are encrypted for your Windows account.", "EasyFlix", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(
            "Search selected media for missing English and Japanese subtitles?\n\nEasyFlix uses configured providers in order, accepts only validated SRT text, excludes machine translations, and never overwrites existing subtitles.",
            "Safe subtitle search", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var cancellationToken = BeginOperation();
        try
        {
            var progress = new Progress<string>(message => Status = message);
            var selected = Items.Where(item => item.IsSelected).ToArray();
            SubtitleRunSummary? subDl = null;
            SubtitleRunSummary? open = null;
            if (!string.IsNullOrWhiteSpace(subDlKey))
            {
                try { subDl = await _subDlSubtitles.DownloadMissingAsync(selected, SplitPaths(ExcludedPaths), subDlKey, maximumDownloads: 45, progress, cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch { Status = "SubDL unavailable; trying OpenSubtitles safelyΓÇª"; }
            }
            if (openCredentials is not null)
            {
                try { open = await _openSubtitles.DownloadMissingAsync(selected, SplitPaths(ExcludedPaths), openCredentials, maximumDownloads: 20, progress, cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch { Status = "OpenSubtitles unavailable; no existing files were changed."; }
            }
            if (subDl is null && open is null)
                throw new InvalidOperationException("The configured subtitle providers are currently unavailable or rejected the saved credentials.");
            var installed = (subDl?.Installed ?? 0) + (open?.Installed ?? 0);
            var rejected = (subDl?.Rejected ?? 0) + (open?.Rejected ?? 0);
            var searches = (subDl?.Searches ?? 0) + (open?.Searches ?? 0);
            var deferred = (subDl?.Deferred ?? 0) + (open?.Deferred ?? 0);
            Status = $"Subtitle run finished: {installed} installed, {rejected} rejected, {searches} searches, {deferred} recent no-match checks deferred" +
                     (subDl?.RateLimited == true || open?.RateLimited == true ? " ┬╖ a provider limit was reached" : string.Empty);
        }
        catch (OperationCanceledException) { Status = "Subtitle search canceled safely. Installed SRTs were kept."; }
        catch (Exception exception)
        {
            Status = "Subtitle search stopped safely.";
            MessageBox.Show(exception.Message, "Subtitle error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndOperation(); }
    }

    private async Task GenerateLocalSubtitlesAsync()
    {
        var selected = Items.Where(item => item.IsSelected).ToArray();
        var modelMessage = LocalSubtitleService.IsModelReady
            ? "The local speech model is ready."
            : "A multilingual speech model (about 460 MB) will be downloaded once.";
        if (MessageBox.Show(
            $"Generate missing subtitles locally for {selected.Length:N0} selected items?\n\n" +
            $"{modelMessage}\n\n" +
            "This can take hours and uses substantial CPU. EasyFlix transcribes English from English audio and Japanese from Japanese audio. Cross-language draft translation is paused because benchmark testing found meaning-changing errors. Existing subtitles are never overwritten.",
            "Offline subtitle generation", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var cancellationToken = BeginOperation();
        try
        {
            var progress = new Progress<string>(message => Status = message);
            var summary = await _localSubtitles.GenerateMissingAsync(selected, SplitPaths(ExcludedPaths), progress, cancellationToken);
            Status = $"Local subtitles complete ┬╖ {summary.Generated} generated ┬╖ {summary.Skipped} already present ┬╖ {summary.DraftTranslationsPaused} draft translations safely paused ┬╖ {summary.JapaneseUnavailable} lacked Japanese audio ┬╖ {summary.Failed} failed";
        }
        catch (OperationCanceledException) { Status = "Local subtitle generation canceled safely. Finished SRTs were kept; run again to resume."; }
        catch (Exception exception)
        {
            Status = "Local subtitle generation stopped safely.";
            MessageBox.Show(exception.Message, "Local subtitle error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndOperation(); }
    }

    private async Task ProcessSelectedAsync()
    {
        if (!EnsureLibraryPath()) return;
        var selected = Items.Where(item => item.IsSelected).ToArray();
        var videoCount = selected.Sum(item => Math.Max(item.VideoCount, 1));
        var removableCount = selected.Count(item => item.IsRemovable);
        var unavailableCount = selected.Count(item => item.IsFolder ? !Directory.Exists(item.SourcePath) : !File.Exists(item.SourcePath));
        var subDlKey = _secrets.Get(SecretStore.SubDlApiKey);
        var openCredentials = GetOpenSubtitlesCredentials();
        var subtitlePlan = string.IsNullOrWhiteSpace(subDlKey) && openCredentials is null
            ? "Verified subtitle downloads will be skipped until a provider is configured."
            : "Configured providers will check missing English/Japanese subtitles safely (SubDL first, then exact-hash OpenSubtitles fallback).";
        var modelPlan = LocalSubtitleService.IsModelReady
            ? "The local Whisper model is ready."
            : "The local Whisper model (about 460 MB) will be downloaded once.";
        if (MessageBox.Show(
            $"Process {selected.Length:N0} selected library items ({videoCount:N0} video files) in one unattended run?\n" +
            $"Removable-drive items: {removableCount:N0} ┬╖ currently unavailable: {unavailableCount:N0}\n\n" +
            "ΓÇó Audit existing embedded and sidecar subtitles\n" +
            "ΓÇó Find and cache confident cover artwork\n" +
            $"ΓÇó {subtitlePlan}\n" +
            $"ΓÇó Generate same-language gaps locally. {modelPlan}\n" +
            "ΓÇó Create missing shortcuts and repair stale targets/artwork\n\n" +
            "Local generation may take hours and use substantial CPU. EasyFlix keeps Windows awake and records resumable progress. Original videos and existing subtitles will never be overwritten.",
            "Run unattended workflow", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var cancellationToken = BeginOperation();
        try
        {
            var subtitleFilesChecked = 0;
            var auditFailed = 0;
            var coversReady = 0;
            var coverFailed = 0;
            var probeAvailable = await MediaProbeService.IsAvailableAsync(cancellationToken: cancellationToken);
            for (var index = 0; index < selected.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = selected[index];
                Status = $"Step 1/4 ┬╖ Finding covers {index + 1:N0}/{selected.Length:N0}: {item.SuggestedName}";
                try
                {
                    await _artwork.SearchAndCacheAsync(item, LibraryPath, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(item.ArtworkPath)) coversReady++;
                }
                catch (OperationCanceledException) { throw; }
                catch { item.ArtworkStatus = "Search failed"; coverFailed++; }
                await Task.Delay(200, cancellationToken);
            }

            SubtitleRunSummary? subDlSubtitles = null;
            SubtitleRunSummary? openSubtitles = null;
            if (probeAvailable && (!string.IsNullOrWhiteSpace(subDlKey) || openCredentials is not null))
            {
                if (!string.IsNullOrWhiteSpace(subDlKey))
                {
                    try
                    {
                        Status = "Step 2/4 ┬╖ Checking SubDL for verified English/Japanese SRTsΓÇª";
                        var progress = new Progress<string>(message => Status = $"Step 2/4 ┬╖ {message}");
                        subDlSubtitles = await _subDlSubtitles.DownloadMissingAsync(
                            selected, SplitPaths(ExcludedPaths), subDlKey, maximumDownloads: 45, progress, cancellationToken);
                        subtitleFilesChecked = Math.Max(subtitleFilesChecked, subDlSubtitles.FilesChecked);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { Status = "Step 2/4 ┬╖ SubDL unavailable; trying the next configured provider."; }
                }
                if (openCredentials is not null)
                {
                    try
                    {
                        Status = "Step 2/4 ┬╖ Checking exact media matches on OpenSubtitlesΓÇª";
                        var progress = new Progress<string>(message => Status = $"Step 2/4 ┬╖ {message}");
                        openSubtitles = await _openSubtitles.DownloadMissingAsync(
                            selected, SplitPaths(ExcludedPaths), openCredentials, maximumDownloads: 20, progress, cancellationToken);
                        subtitleFilesChecked = Math.Max(subtitleFilesChecked, openSubtitles.FilesChecked);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { Status = "Step 2/4 ┬╖ OpenSubtitles unavailable; continuing safely."; }
                }
            }
            else if (probeAvailable)
            {
                for (var index = 0; index < selected.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = selected[index];
                    Status = $"Step 2/4 ┬╖ Auditing {index + 1:N0}/{selected.Length:N0}: {item.SuggestedName}";
                    try
                    {
                        var audit = await _probe.AuditAsync(item, cancellationToken: cancellationToken);
                        item.SubtitleStatus = audit.Summary;
                        subtitleFilesChecked += audit.Videos;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { item.SubtitleStatus = "Audit failed"; auditFailed++; }
                }
            }
            else
            {
                foreach (var item in selected) item.SubtitleStatus = "FFprobe required";
                auditFailed = selected.Length;
            }

            cancellationToken.ThrowIfCancellationRequested();
            LocalSubtitleSummary? local = null;
            if (probeAvailable && await LocalSubtitleService.IsFfmpegAvailableAsync(cancellationToken))
            {
                try
                {
                    Status = "Step 3/4 ┬╖ Preparing offline subtitle fallbackΓÇª";
                    var progress = new Progress<string>(message => Status = $"Step 3/4 ┬╖ {message}");
                    local = await _localSubtitles.GenerateMissingAsync(selected, SplitPaths(ExcludedPaths), progress, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch { Status = "Step 3/4 ┬╖ Offline subtitle fallback unavailable; continuing safely."; }
            }

            cancellationToken.ThrowIfCancellationRequested();
            Status = "Step 4/4 ┬╖ Creating missing shortcutsΓÇª";
            var shortcutSummary = _library.Apply(_planner.BuildPlan(selected, LibraryPath), LibraryPath, cancellationToken);
            var subtitleResult = subDlSubtitles is null && openSubtitles is null
                ? "subtitle download skipped"
                : $"{(subDlSubtitles?.Installed ?? 0) + (openSubtitles?.Installed ?? 0)} SRTs installed, " +
                  $"{(subDlSubtitles?.Rejected ?? 0) + (openSubtitles?.Rejected ?? 0)} rejected, " +
                  $"{(subDlSubtitles?.Deferred ?? 0) + (openSubtitles?.Deferred ?? 0)} recent no-match checks deferred";
            var localResult = local is null
                ? "offline generation skipped"
                : $"{local.Generated} SRTs generated locally, {local.DraftTranslationsPaused} draft translations safely paused, {local.JapaneseUnavailable} Japanese gaps lacked Japanese audio, {local.Failed} failed";
            Status = $"Workflow complete ┬╖ {subtitleFilesChecked} subtitle files checked ({auditFailed} items unavailable) ┬╖ " +
                $"{coversReady} covers ({coverFailed} failed) ┬╖ {subtitleResult} ┬╖ {localResult} ┬╖ " +
                $"{shortcutSummary.Created} shortcuts created, {shortcutSummary.Updated} refreshed, {shortcutSummary.Skipped} already current";
        }
        catch (OperationCanceledException)
        {
            Status = "Workflow canceled safely. Completed covers, SRTs, and shortcuts were kept.";
        }
        catch (Exception exception)
        {
            Status = "Workflow stopped safely.";
            MessageBox.Show(exception.Message, "Workflow error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndOperation(); }
    }

    private void SetSelection(bool selected)
    {
        foreach (var item in Items) item.IsSelected = selected;
        ApplyCommand.RaiseCanExecuteChanged();
    }

    private void RefreshCounts()
    {
        MovieCount = Items.Count(item => item.Kind == MediaKind.Movie);
        TvCount = Items.Count(item => item.Kind == MediaKind.TvShow);
        AnimeCount = Items.Count(item => item.Kind == MediaKind.Anime);
    }

    private bool MatchesSearch(object value)
    {
        if (value is not MediaItem item || string.IsNullOrWhiteSpace(SearchText)) return true;
        return item.SuggestedName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || item.OriginalName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || item.SourcePath.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || item.SeriesTitle.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private CancellationToken BeginOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        return _operationCancellation.Token;
    }

    private void EndOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        IsBusy = false;
    }

    private void CancelOperation()
    {
        Status = "Canceling safely after the current fileΓÇª";
        _operationCancellation?.Cancel();
    }

    private bool EnsureLibraryPath()
    {
        if (!string.IsNullOrWhiteSpace(LibraryPath))
        {
            try { _ = Path.GetFullPath(LibraryPath); return true; }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        MessageBox.Show("Choose a valid unified library folder first.", "EasyFlix", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private OpenSubtitlesCredentials? GetOpenSubtitlesCredentials()
    {
        var apiKey = _secrets.Get(SecretStore.OpenSubtitlesApiKey);
        var username = _secrets.Get(SecretStore.OpenSubtitlesUsername);
        var password = _secrets.Get(SecretStore.OpenSubtitlesPassword);
        return string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)
            ? null
            : new OpenSubtitlesCredentials(apiKey, username, password);
    }

    private static string[] SplitPaths(string value) => value
        .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
