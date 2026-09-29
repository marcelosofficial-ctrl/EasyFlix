using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EasyFlix.App.ViewModels;
using EasyFlix.Core.Models;

namespace EasyFlix.App;

public partial class BrowseWindow : Window
{
    private enum ShellState
    {
        ExpandedHome,
        Browsing,
        FocusedTitle,
        BackToBrowsing
    }

    private const double ExpandedSidebarWidth = 168;
    private const int ChromeMotionMs = 220;
    private const int FocusMotionMs = 260;

    private readonly BrowseViewModel _viewModel = new();
    private readonly DispatcherTimer _heroTimer = new() { Interval = TimeSpan.FromSeconds(12) };
    private readonly DispatcherTimer _detailsRefreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private ShellState _shellState = ShellState.ExpandedHome;
    private ShellState _returnShellState = ShellState.ExpandedHome;
    private bool _sidebarCompact;
    private bool _smartScrollActive;
    private bool _detailsRowsRefreshing;
    private bool _detailsOpening;
    private bool _chromeTransitionActive;
    private bool _detailsClosing;
    private bool _startupInitialized;
    private double _browseScrollOffsetBeforeDetails;
    private IInputElement? _browseFocusBeforeDetails;
    private TitleDetailsWindow? _embeddedDetailsWindow;

    public BrowseWindow()
    {
        InitializeComponent();
        BrowseHomeButton.Content = "< Home";
        FocusedBackButton.Content = "< Back";
        DataContext = _viewModel;
        ContentRendered += BrowseWindow_ContentRendered;
        PreviewMouseWheel += BrowseWindow_PreviewMouseWheel;
        PreviewKeyDown += BrowseWindow_PreviewKeyDown;
        _detailsRefreshTimer.Tick += DetailsRefreshTimer_Tick;
        _heroTimer.Tick += (_, _) =>
        {
            if (_viewModel.IsHome && IsActive && !_viewModel.IsBusy && _shellState != ShellState.FocusedTitle)
                _viewModel.ShowNextHero();
        };
        Closed += (_, _) =>
        {
            _heroTimer.Stop();
            _detailsRefreshTimer.Stop();
            _viewModel.Dispose();
        };
    }

    private async void BrowseWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (_startupInitialized)
            return;

        _startupInitialized = true;

        // Let WPF paint the dark branded startup surface before catalog
        // initialization performs any synchronous work on the UI thread.
        await Dispatcher.Yield(DispatcherPriority.Background);
        await _viewModel.InitializeAsync();
        _heroTimer.Start();

var fade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            StartupOverlay.Visibility = Visibility.Collapsed;
            StartupOverlay.IsHitTestVisible = false;
            StartupOverlay.BeginAnimation(OpacityProperty, null);
        };
        StartupOverlay.BeginAnimation(OpacityProperty, fade);

        _ = Dispatcher.BeginInvoke(
            new Action(async () =>
            {
                await _viewModel.HydratePersonalizedStartupAsync();
                await _viewModel.ReconcileAfterStartupAsync();
            }),
            DispatcherPriority.ContextIdle);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _viewModel.SearchText = SearchBox.Text;
        if (_shellState == ShellState.FocusedTitle || _shellState == ShellState.BackToBrowsing)
            return;

        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            SetShellState(ShellState.Browsing, animate: true);
        else if (_viewModel.IsHome && MainScrollViewer.VerticalOffset <= 24)
            SetShellState(ShellState.ExpandedHome, animate: true);
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus();
    }

    private async void Section_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string section }) return;

        if (_shellState == ShellState.FocusedTitle)
            await ExitFocusedTitleAsync(restoreBrowseContext: false);

        _viewModel.SelectSection(section);
        if (string.Equals(section, "Home", StringComparison.OrdinalIgnoreCase))
        {
            MainScrollViewer.ScrollToTop();
            SetShellState(ShellState.ExpandedHome, animate: true);
        }
        else
        {
            SetShellState(ShellState.Browsing, animate: true);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await _viewModel.RefreshAsync();

    private async void SyncMetadata_Click(object sender, RoutedEventArgs e)
        => await _viewModel.SyncMetadataAsync();

    private void AddMedia_Click(object sender, RoutedEventArgs e)
        => new MediaIntakeWindow { Owner = this }.ShowDialog();

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        new PlaybackHistoryWindow(_viewModel.Store) { Owner = this }.ShowDialog();
        await _viewModel.RefreshFavoritesAsync();
        await _viewModel.RefreshContinueWatchingAsync();
        await _viewModel.RefreshUpNextAsync();
    }

    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        new FirstRunWindow { Owner = this }.ShowDialog();
        await _viewModel.RefreshAsync();
    }

    private async void Library_Click(object sender, RoutedEventArgs e)
    {
        new LibraryMaintenanceWindow { Owner = this }.ShowDialog();
        await _viewModel.RefreshAsync();
    }

    private void Hero_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Hero is { } title) OpenDetails(title);
    }

    private void HeroPrevious_Click(object sender, RoutedEventArgs e)
        => _viewModel.ShowPreviousHero();

    private void HeroNext_Click(object sender, RoutedEventArgs e)
        => _viewModel.ShowNextHero();

    private void TitleCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CatalogTitleSummary title }) OpenDetails(title);
    }

    private void ContinueWatching_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ContinueWatchingItem item })
            OpenDetails(item.Title, item);
    }

    private void UpNext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: UpNextItem item })
            OpenDetails(item.Title, selectedMediaFileId: item.Next.MediaFileId);
    }

    private async void OpenDetails(
        CatalogTitleSummary title,
        ContinueWatchingItem? continueWatching = null,
        string? selectedMediaFileId = null)
    {
        if (_detailsOpening || _shellState == ShellState.FocusedTitle)
            return;

        _detailsOpening = true;
        var priorState = _viewModel.IsHome && MainScrollViewer.VerticalOffset <= 48
            ? ShellState.ExpandedHome
            : ShellState.Browsing;
        _returnShellState = priorState;
        _browseScrollOffsetBeforeDetails = MainScrollViewer.VerticalOffset;
        _browseFocusBeforeDetails = Keyboard.FocusedElement;

        var progress = continueWatching?.Progress;
        var initialMediaFileId = progress?.MediaFileId ?? selectedMediaFileId;
        var details = new TitleDetailsWindow(
            _viewModel.Store,
            title.Id,
            initialMediaFileId,
            progress?.PositionSeconds,
            progress?.DurationSeconds);

        try
        {
            var contentTask = details.PrepareEmbeddedContentAsync(this);

            if (priorState == ShellState.ExpandedHome)
            {
                SetShellState(ShellState.Browsing, animate: true);
                await Task.Delay(ChromeMotionMs);
            }

            var content = await contentTask;
            _embeddedDetailsWindow = details;
            FocusedTitleContent.Content = content;
            SetShellState(ShellState.FocusedTitle, animate: true);
            BeginFocusedTitleTransition();
            _detailsRefreshTimer.Start();
        }
        catch (Exception ex)
        {
            if (priorState == ShellState.ExpandedHome)
                SetShellState(ShellState.ExpandedHome, animate: true);

            MessageBox.Show(this, "Could not open title details.\n\n" + ex.Message, "EasyFlix", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _detailsOpening = false;
        }
    }

    private void BeginFocusedTitleTransition()
    {
        BrowseSurface.IsHitTestVisible = false;
        FocusedTitleHost.IsHitTestVisible = true;

        AnimateOpacity(BrowseSurface, 0.32, FocusMotionMs);
        AnimateTranslate(BrowseFocusTransform, -10, FocusMotionMs);
        AnimateOpacity(FocusedTitleHost, 1, FocusMotionMs);
        AnimateTranslate(FocusedTitleTransform, 0, FocusMotionMs);

        Dispatcher.BeginInvoke(() => FocusedBackButton.Focus(), DispatcherPriority.Loaded);
    }

    private async void BackFromDetails_Click(object sender, RoutedEventArgs e)
        => await ExitFocusedTitleAsync(restoreBrowseContext: true);

    private void BrowseHome_Click(object sender, RoutedEventArgs e)
        => NavigateHome();

    private void NavigateHome()
    {
        SearchBox.Text = string.Empty;
        _viewModel.SelectSection("Home");
        MainScrollViewer.ScrollToTop();
        SetShellState(ShellState.ExpandedHome, animate: true);
        Dispatcher.BeginInvoke(() => MainScrollViewer.Focus(), DispatcherPriority.Loaded);
    }

    private async void BrowseWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.BrowserBack))
            return;

        if (_shellState == ShellState.FocusedTitle)
        {
            e.Handled = true;
            await ExitFocusedTitleAsync(restoreBrowseContext: true);
            return;
        }

        if (!_viewModel.IsHome || !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            e.Handled = true;
            NavigateHome();
        }
    }

    private async Task ExitFocusedTitleAsync(bool restoreBrowseContext)
    {
        if (_detailsClosing || _shellState != ShellState.FocusedTitle)
            return;

        _detailsClosing = true;
        _detailsRefreshTimer.Stop();
        _shellState = ShellState.BackToBrowsing;

        BrowseSurface.IsHitTestVisible = true;
        AnimateOpacity(BrowseSurface, 1, FocusMotionMs);
        AnimateTranslate(BrowseFocusTransform, 0, FocusMotionMs);
        AnimateOpacity(FocusedTitleHost, 0, FocusMotionMs);
        AnimateTranslate(FocusedTitleTransform, 36, FocusMotionMs);

        await Task.Delay(FocusMotionMs);

        FocusedTitleHost.IsHitTestVisible = false;
        FocusedTitleContent.Content = null;
        _embeddedDetailsWindow = null;

        if (restoreBrowseContext)
        {
            MainScrollViewer.ScrollToVerticalOffset(_browseScrollOffsetBeforeDetails);
            SetShellState(_returnShellState, animate: true);
            if (_browseFocusBeforeDetails is { } priorFocus)
                _ = Dispatcher.BeginInvoke(() => priorFocus.Focus(), DispatcherPriority.Loaded);
        }
        else
        {
            SetShellState(ShellState.Browsing, animate: true);
        }

        try
        {
            await _viewModel.RefreshFavoritesAsync();
            await _viewModel.RefreshContinueWatchingAsync();
            await _viewModel.RefreshUpNextAsync();
        }
        finally
        {
            _detailsClosing = false;
        }
    }

    private async void DetailsRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_detailsRowsRefreshing || _shellState != ShellState.FocusedTitle)
            return;

        _detailsRowsRefreshing = true;
        try
        {
            await _viewModel.RefreshContinueWatchingAsync();
            await _viewModel.RefreshUpNextAsync();
        }
        finally
        {
            _detailsRowsRefreshing = false;
        }
    }

    private void MainScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_shellState is ShellState.FocusedTitle or ShellState.BackToBrowsing ||
            _smartScrollActive || _chromeTransitionActive)
            return;

        ReconcileShellStateFromScroll();
    }

    private void ReconcileShellStateFromScroll()
    {
        if (_shellState is ShellState.FocusedTitle or ShellState.BackToBrowsing)
            return;

        if (!_viewModel.IsHome || !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            SetShellState(ShellState.Browsing, animate: true);
            return;
        }

        var offset = MainScrollViewer.VerticalOffset;
        if (offset <= 2)
            SetShellState(ShellState.ExpandedHome, animate: true);
        else if (offset >= 24)
            SetShellState(ShellState.Browsing, animate: true);
    }

    private void SetShellState(ShellState state, bool animate)
    {
        if (_shellState == state)
            return;

        _shellState = state;
        var compact = state != ShellState.ExpandedHome;
        ApplySidebarState(compact, animate);
        ApplyHeroState(compact, animate);
    }

    private void ApplySidebarState(bool compact, bool animate)
    {
        if (_sidebarCompact == compact)
            return;

        _sidebarCompact = compact;
        if (!animate)
        {
            _chromeTransitionActive = false;
            SidebarColumn.Width = new GridLength(compact ? 0 : ExpandedSidebarWidth);
            FullSidebar.Opacity = compact ? 0 : 1;
            FullSidebarTransform.BeginAnimation(TranslateTransform.XProperty, null);
            FullSidebarTransform.X = compact ? -96 : 0;
            FullSidebar.IsHitTestVisible = !compact;
            CompactSidebar.Opacity = 0;
            CompactSidebar.IsHitTestVisible = false;
            BrowseSidebarTransform.BeginAnimation(TranslateTransform.XProperty, null);
            BrowseSidebarTransform.X = 0;
            return;
        }

        _chromeTransitionActive = true;
        BrowseSidebarTransform.BeginAnimation(TranslateTransform.XProperty, null);
        BrowseSidebarTransform.X = 0;
        CompactSidebar.BeginAnimation(UIElement.OpacityProperty, null);
        CompactSidebar.Opacity = 0;
        CompactSidebar.IsHitTestVisible = false;

        if (compact)
        {
            FullSidebar.IsHitTestVisible = false;
            AnimateOpacity(FullSidebar, 0, ChromeMotionMs);

            FullSidebarTransform.BeginAnimation(TranslateTransform.XProperty, null);
            var sidebarOut = CreateMotion(
                FullSidebarTransform.X,
                -96,
                ChromeMotionMs,
                FillBehavior.HoldEnd);
            sidebarOut.Completed += (_, _) =>
            {
                if (!_sidebarCompact)
                {
                    _chromeTransitionActive = false;
                    Dispatcher.BeginInvoke(ReconcileShellStateFromScroll, DispatcherPriority.Background);
                    return;
                }

                SidebarColumn.Width = new GridLength(0);
                FullSidebarTransform.BeginAnimation(TranslateTransform.XProperty, null);
                FullSidebarTransform.X = -96;
                _chromeTransitionActive = false;
                Dispatcher.BeginInvoke(ReconcileShellStateFromScroll, DispatcherPriority.Background);
            };
            FullSidebarTransform.BeginAnimation(
                TranslateTransform.XProperty,
                sidebarOut,
                HandoffBehavior.SnapshotAndReplace);
            return;
        }

        SidebarColumn.Width = new GridLength(ExpandedSidebarWidth);
        FullSidebar.IsHitTestVisible = true;
        AnimateOpacity(FullSidebar, 1, ChromeMotionMs);

        FullSidebarTransform.BeginAnimation(TranslateTransform.XProperty, null);
        var sidebarIn = CreateMotion(
            FullSidebarTransform.X,
            0,
            ChromeMotionMs,
            FillBehavior.HoldEnd);
        sidebarIn.Completed += (_, _) =>
        {
            if (_sidebarCompact)
            {
                _chromeTransitionActive = false;
                Dispatcher.BeginInvoke(ReconcileShellStateFromScroll, DispatcherPriority.Background);
                return;
            }

            FullSidebarTransform.BeginAnimation(TranslateTransform.XProperty, null);
            FullSidebarTransform.X = 0;
            _chromeTransitionActive = false;
            Dispatcher.BeginInvoke(ReconcileShellStateFromScroll, DispatcherPriority.Background);
        };
        FullSidebarTransform.BeginAnimation(
            TranslateTransform.XProperty,
            sidebarIn,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void ApplyHeroState(bool collapsed, bool animate)
    {
        var targetOpacity = collapsed ? 0.0 : 1.0;
        var targetScale = collapsed ? 0.97 : 1.0;

        if (!animate)
        {
            HeroFeature.Opacity = targetOpacity;
            HeroScaleTransform.ScaleX = targetScale;
            HeroScaleTransform.ScaleY = targetScale;
            return;
        }

        AnimateOpacity(HeroFeature, targetOpacity, ChromeMotionMs);
        AnimateScale(HeroScaleTransform, targetScale, ChromeMotionMs);
    }

    private static DoubleAnimation CreateMotion(
        double from,
        double to,
        int milliseconds,
        FillBehavior fillBehavior = FillBehavior.Stop)
        => new(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = fillBehavior
        };

    private static void AnimateOpacity(UIElement element, double target, int milliseconds)
    {
        var from = element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = target;
        element.BeginAnimation(
            UIElement.OpacityProperty,
            CreateMotion(from, target, milliseconds),
            HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateTranslate(TranslateTransform transform, double target, int milliseconds)
    {
        var from = transform.X;
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = target;
        transform.BeginAnimation(
            TranslateTransform.XProperty,
            CreateMotion(from, target, milliseconds),
            HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateScale(ScaleTransform transform, double target, int milliseconds)
    {
        var fromX = transform.ScaleX;
        var fromY = transform.ScaleY;
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        transform.ScaleX = target;
        transform.ScaleY = target;
        transform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            CreateMotion(fromX, target, milliseconds),
            HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            CreateMotion(fromY, target, milliseconds),
            HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateHomeIntoRows(double target)
    {
        target = Math.Clamp(target, 0, MainScrollViewer.ScrollableHeight);
        var start = MainScrollViewer.VerticalOffset;
        if (Math.Abs(target - start) < 2) return;

        _smartScrollActive = true;
        var distance = target - start;
        HomeTransitionTransform.BeginAnimation(TranslateTransform.YProperty, null);
        HomeTransitionTransform.Y = 0;
        var animation = new DoubleAnimation(0, -distance, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            MainScrollViewer.ScrollToVerticalOffset(target);
            HomeTransitionTransform.BeginAnimation(TranslateTransform.YProperty, null);
            HomeTransitionTransform.Y = 0;
            _smartScrollActive = false;
        };
        HomeTransitionTransform.BeginAnimation(TranslateTransform.YProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void BrowseWindow_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_shellState == ShellState.FocusedTitle)
            return;

        if (e.OriginalSource is not DependencyObject source)
            return;

        if (_viewModel.IsHome && e.Delta < 0 &&
            MainScrollViewer.VerticalOffset < Math.Max(80, HeroFeature.ActualHeight * 0.75))
        {
            e.Handled = true;
            if (_smartScrollActive) return;

            SetShellState(ShellState.Browsing, animate: true);
            var anchor = HomeRowsAnchor.TranslatePoint(new Point(0, 0), MainScrollViewer).Y
                + MainScrollViewer.VerticalOffset;
            if (anchor > MainScrollViewer.VerticalOffset + 24)
                AnimateHomeIntoRows(Math.Max(0, anchor - 8));
            return;
        }

        var current = source;
        while (current is not null && !ReferenceEquals(current, MainScrollViewer))
        {
            if (current is ScrollViewer nested &&
                !ReferenceEquals(nested, MainScrollViewer) &&
                nested.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
            {
                var step = Math.Clamp(Math.Abs(e.Delta) * 1.6, 160, 280);
                var target = MainScrollViewer.VerticalOffset - Math.Sign(e.Delta) * step;
                MainScrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
                e.Handled = true;
                return;
            }

            current = VisualTreeHelper.GetParent(current);
        }
    }
}
