using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UsageDeck.Core.Formatting;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace UsageDeck.App;

public sealed partial class MainPage : Page, INotifyPropertyChanged
{
    private readonly ProviderTabViewModel _allProvidersTab = new(ProviderId.All, ProviderId.All.DisplayName);
    private readonly ProviderRefreshCoordinator _refreshCoordinator;
    private readonly HashSet<ProviderTabViewModel> _providerRefreshesInProgress = [];
    private readonly DispatcherTimer _presentationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly UISettings _uiSettings = new();
    private readonly string _appVersionText = $"v{App.VersionNumber}";
    private bool _hasCompletedInitialLoad;
    private bool _hasShownInitialContent;
    private bool _isUpdateOperationInProgress;
    private bool _showCodexSparkCard = true;
    private int _refreshOperationsInProgress;
    private ResetTimeDisplayMode _resetTimeDisplay = ResetTimeDisplayMode.Countdown;
    private Storyboard? _skeletonShimmerStoryboard;
    private ProviderTabViewModel _selectedProvider = null!;
    private TimeDisplayPrecision _timeDisplayPrecision = TimeDisplayPrecision.Seconds;
    private string _themeToggleGlyph = "\uE708";
    private string _themeToggleLabel = "Switch to dark theme";
    private UsageValueDisplayMode _usageValueDisplay = UsageValueDisplayMode.Used;

    public MainPage()
    {
        this.InitializeComponent();
        this._refreshCoordinator = ((App)Application.Current).RefreshCoordinator;
        App app = (App)Application.Current;
        this._showCodexSparkCard = app.CurrentSettings.ShowCodexSparkCard;
        this._resetTimeDisplay = app.CurrentSettings.ResetTimeDisplay;
        this._usageValueDisplay = app.CurrentSettings.UsageValueDisplay;
        this._refreshTimer.Interval = TimeSpan.FromMinutes(app.CurrentSettings.RefreshIntervalMinutes);
        this.ApplyPresentationCadence(app.CurrentSettings.RefreshIntervalMinutes);
        app.SettingsChanged += this.App_SettingsChanged;
        app.AccessibilityChanged += this.App_AccessibilityChanged;
        app.ProviderStatusStateChanged += this.App_ProviderStatusStateChanged;
        app.UpdateStateChanged += this.App_UpdateStateChanged;
        this.Tabs = [];
        this.Providers = [];
        this.ApplySettings(app.CurrentSettings);
        this.DataContext = this;
        this.Loaded += this.MainPage_Loaded;
        this.Unloaded += this.MainPage_Unloaded;
        this.ActualThemeChanged += this.MainPage_ActualThemeChanged;
        this._presentationTimer.Tick += this.PresentationTimer_Tick;
        this._refreshTimer.Tick += this.RefreshTimer_Tick;
        this.RefreshProviderStatusPresentation();
        this.RefreshUpdatePresentation();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProviderTabViewModel> Tabs { get; }

    public ObservableCollection<ProviderTabViewModel> Providers { get; }

    public string AllSummaryText => this.Providers.Count == 1
        ? "1 enabled provider"
        : $"{this.Providers.Count} enabled providers";

    public string AppVersionText => this._appVersionText;

    public string ThemeToggleGlyph
    {
        get => this._themeToggleGlyph;
        private set
        {
            if (this._themeToggleGlyph == value)
            {
                return;
            }

            this._themeToggleGlyph = value;
            this.OnPropertyChanged();
        }
    }

    public string ThemeToggleLabel
    {
        get => this._themeToggleLabel;
        private set
        {
            if (this._themeToggleLabel == value)
            {
                return;
            }

            this._themeToggleLabel = value;
            this.OnPropertyChanged();
        }
    }

    public ProviderTabViewModel SelectedProvider
    {
        get => this._selectedProvider;
        set
        {
            if (value is null || ReferenceEquals(this._selectedProvider, value))
            {
                return;
            }

            this._selectedProvider?.IsSelected = false;
            this._selectedProvider = value;
            this._selectedProvider.IsSelected = true;
            this.OnPropertyChanged();
        }
    }

    public Task RefreshAllAsync() => this.RefreshProvidersAsync(this.Providers);

    internal void SelectProvider(ProviderId providerId)
    {
        ProviderTabViewModel? provider = this.Providers.FirstOrDefault(candidate => candidate.Id == providerId);
        if (provider is not null)
        {
            this.SelectedProvider = provider;
        }
    }

    private async Task RefreshProvidersAsync(IEnumerable<ProviderTabViewModel> providers)
    {
        ProviderTabViewModel[] providersToRefresh = providers.Where(provider => !provider.IsAll).ToArray();
        if (providersToRefresh.Length == 0)
        {
            return;
        }

        this._refreshOperationsInProgress++;
        this._allProvidersTab.IsLoading = true;
        try
        {
            Task[] refreshes = providersToRefresh.Select(this.RefreshProviderAsync).ToArray();
            await Task.WhenAll(refreshes);
        }
        finally
        {
            this._refreshOperationsInProgress--;
            this._allProvidersTab.IsLoading = this._refreshOperationsInProgress > 0;
        }
    }

    private async Task RefreshProviderAsync(ProviderTabViewModel provider)
    {
        provider.IsLoading = true;
        ProviderSnapshot snapshot = await this._refreshCoordinator.RefreshAsync(provider.Id);
        provider.ApplySnapshot(
            snapshot,
            DateTimeOffset.Now,
            this._timeDisplayPrecision,
            this._showCodexSparkCard,
            this._resetTimeDisplay,
            this._usageValueDisplay);
        if (ReferenceEquals(provider, this.SelectedProvider))
        {
            this.ShowInitialContent();
        }
    }

    private async Task EnsureSelectedProviderFreshAsync(ProviderTabViewModel provider)
    {
        if (provider.IsAll)
        {
            ProviderTabViewModel[] providersDueForRefresh = this.Providers
                .Where(candidate => candidate.NeedsRefresh(DateTimeOffset.Now, this._refreshTimer.Interval))
                .ToArray();
            if (providersDueForRefresh.Length > 0)
            {
                await this.RefreshProvidersAsync(providersDueForRefresh);
            }
            else
            {
                this._allProvidersTab.IsLoading = this._refreshOperationsInProgress > 0;
            }

            this.ShowInitialContent();
            return;
        }

        if (!provider.NeedsRefresh(DateTimeOffset.Now, this._refreshTimer.Interval)
            || !this._providerRefreshesInProgress.Add(provider))
        {
            return;
        }

        try
        {
            await this.RefreshProviderAsync(provider);
        }
        finally
        {
            this._providerRefreshesInProgress.Remove(provider);
        }
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        this._presentationTimer.Start();
        this._refreshTimer.Start();
        this.StartSkeletonShimmer();
        if (this._hasCompletedInitialLoad)
        {
            return;
        }

        this._hasCompletedInitialLoad = true;
        await this.EnsureSelectedProviderFreshAsync(this.SelectedProvider);
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        this._presentationTimer.Stop();
        this._refreshTimer.Stop();
        this.StopSkeletonShimmer();
    }

    private void ShowInitialContent()
    {
        if (this._hasShownInitialContent)
        {
            return;
        }

        this._hasShownInitialContent = true;
        this.UsageContent.Visibility = Visibility.Visible;
        this.UsageContent.IsHitTestVisible = true;
        this.UsageContent.UpdateLayout();

        if (!this._uiSettings.AnimationsEnabled)
        {
            this.StopSkeletonShimmer();
            this.UsageContent.Opacity = 1;
            this.InitialLoadingState.Visibility = Visibility.Collapsed;
            return;
        }

        this.InitialLoadingState.IsHitTestVisible = false;
        Duration duration = new(TimeSpan.FromMilliseconds(200));
        QuadraticEase easing = new() { EasingMode = EasingMode.EaseOut };
        DoubleAnimation fadeOut = new()
        {
            From = 1,
            To = 0,
            Duration = duration,
            EasingFunction = easing,
        };
        DoubleAnimation fadeIn = new()
        {
            From = 0,
            To = 1,
            Duration = duration,
            EasingFunction = easing,
        };
        Storyboard.SetTarget(fadeOut, this.InitialLoadingState);
        Storyboard.SetTargetProperty(fadeOut, nameof(UIElement.Opacity));
        Storyboard.SetTarget(fadeIn, this.UsageContent);
        Storyboard.SetTargetProperty(fadeIn, nameof(UIElement.Opacity));

        Storyboard transition = new();
        transition.Children.Add(fadeOut);
        transition.Children.Add(fadeIn);
        transition.Completed += (_, _) =>
        {
            this.StopSkeletonShimmer();
            this.InitialLoadingState.Visibility = Visibility.Collapsed;
            this.UsageContent.Opacity = 1;
        };
        transition.Begin();
    }

    private void StartSkeletonShimmer()
    {
        if (this._hasShownInitialContent ||
            this._skeletonShimmerStoryboard is not null ||
            !this._uiSettings.AnimationsEnabled ||
            App.IsHighContrastEnabled)
        {
            return;
        }

        TranslateTransform shimmerTransform =
            (TranslateTransform)this.Resources["SkeletonShimmerTransform"];
        DoubleAnimation shimmer = new()
        {
            From = -1.25,
            To = 1.25,
            Duration = new Duration(TimeSpan.FromMilliseconds(1400)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(shimmer, shimmerTransform);
        Storyboard.SetTargetProperty(shimmer, nameof(TranslateTransform.X));

        this._skeletonShimmerStoryboard = new Storyboard();
        this._skeletonShimmerStoryboard.Children.Add(shimmer);
        this._skeletonShimmerStoryboard.Begin();
    }

    private void StopSkeletonShimmer()
    {
        if (this._skeletonShimmerStoryboard is null)
        {
            return;
        }

        this._skeletonShimmerStoryboard.Stop();
        this._skeletonShimmerStoryboard = null;
    }

    private void PresentationTimer_Tick(object? sender, object e)
        => this.UpdatePresentationTime(DateTimeOffset.Now);

    private void UpdatePresentationTime(DateTimeOffset now)
    {
        foreach (ProviderTabViewModel provider in this.Providers)
        {
            provider.UpdateTime(
                now,
                this._timeDisplayPrecision,
                this._resetTimeDisplay,
                this._usageValueDisplay);
        }
    }

    private async void RefreshTimer_Tick(object? sender, object e)
    {
        if (this.SelectedProvider.IsAll)
        {
            await this.RefreshProvidersAsync(this.Providers);
            return;
        }

        await this.RefreshProviderAsync(this.SelectedProvider);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await this.RefreshAllAsync();

    private async void RetryProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (this.SelectedProvider.IsAll
            || this.SelectedProvider.IsLoading
            || sender is not Button retryButton)
        {
            return;
        }

        retryButton.IsEnabled = false;
        try
        {
            await this.RefreshProviderAsync(this.SelectedProvider);
        }
        finally
        {
            retryButton.IsEnabled = true;
        }
    }

    private async void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        App app = (App)Application.Current;
        AppThemePreference nextTheme = this.ActualTheme == ElementTheme.Dark
            ? AppThemePreference.Light
            : AppThemePreference.Dark;

        try
        {
            await app.UpdateSettingsAsync(settings => settings with { Theme = nextTheme });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            this.SelectedProvider.ShowWarning("The theme preference could not be saved.");
        }
    }

    private void ProvidersButton_Click(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).ShowSettingsWindow();

    private async void ProviderStatusFlyout_Opening(object? sender, object e)
    {
        this.RefreshProviderStatusPresentation();
        App app = (App)Application.Current;
        bool hasAnyStatus = this.Providers.Any(provider =>
            app.StatusCoordinator.GetSnapshot(provider.Id) is not null);
        if (!hasAnyStatus && !app.IsProviderStatusRefreshInProgress)
        {
            await app.RefreshProviderStatusesAsync();
        }
    }

    private async void ProviderStatusRefreshButton_Click(object sender, RoutedEventArgs e) =>
        await ((App)Application.Current).RefreshProviderStatusesAsync();

    private async void OfficialStatusLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not Uri uri)
        {
            return;
        }

        this.ProviderStatusFlyout.Hide();
        await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private async void UpdateActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (this._isUpdateOperationInProgress)
        {
            return;
        }

        App app = (App)Application.Current;
        AppUpdateService updater = app.UpdateService;
        if (updater.AvailableUpdate is not AppUpdateAvailability available)
        {
            this.RefreshUpdatePresentation();
            return;
        }

        bool isDownloaded = updater.IsUpdateDownloaded;
        ContentDialog confirmation = new()
        {
            XamlRoot = this.XamlRoot,
            Title = isDownloaded ? "Restart to update UsageDeck?" : "Update UsageDeck?",
            Content = isDownloaded
                ? $"Version {available.Version} is ready. UsageDeck needs to close and restart to finish installing it."
                : $"Version {available.Version} is available. UsageDeck will download it, close, and restart to finish updating.",
            PrimaryButtonText = isDownloaded ? "Restart now" : "Update",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        this._isUpdateOperationInProgress = true;
        this.UpdateActionButton.IsEnabled = false;
        try
        {
            if (!isDownloaded)
            {
                this.UpdateActionButton.Content = "Downloading… 0%";
                Progress<int> progress = new(value =>
                    this.UpdateActionButton.Content = $"Downloading… {value}%");
                await updater.DownloadUpdateAsync(progress, CancellationToken.None);
                app.NotifyUpdateStateChanged();
            }

            this.UpdateActionButton.Content = "Restarting…";
            app.RestartForUpdate();
        }
        catch (Exception exception) when (AppUpdateService.IsExpectedFailure(exception))
        {
            app.NotifyUpdateStateChanged();
            ContentDialog error = new()
            {
                XamlRoot = this.XamlRoot,
                Title = "UsageDeck couldn’t update",
                Content = "The update could not be installed. UsageDeck is still running on the current version.",
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close,
            };
            await error.ShowAsync();
        }
        finally
        {
            this._isUpdateOperationInProgress = false;
            this.RefreshUpdatePresentation();
        }
    }

    private void MainPage_ActualThemeChanged(FrameworkElement sender, object args)
    {
        this.UpdateThemeToggleAppearance();
        this.RefreshProviderStatusPresentation();
        this.RefreshVisualStates();
    }

    private void App_AccessibilityChanged()
    {
        this.RefreshProviderStatusPresentation();
        this.RefreshVisualStates();
        if (App.IsHighContrastEnabled)
        {
            this.StopSkeletonShimmer();
            return;
        }

        this.StartSkeletonShimmer();
    }

    private void App_UpdateStateChanged() => this.RefreshUpdatePresentation();

    private void App_ProviderStatusStateChanged() => this.RefreshProviderStatusPresentation();

    private void RefreshProviderStatusPresentation()
    {
        App app = (App)Application.Current;
        bool monitoringEnabled = app.CurrentSettings.IsStatusMonitoringEnabled;
        foreach (ProviderTabViewModel provider in this.Providers)
        {
            provider.ApplyServiceStatus(
                app.StatusCoordinator.GetSnapshot(provider.Id),
                app.StatusCoordinator.GetOfficialStatusUri(provider.Id),
                monitoringEnabled);
        }

        this.ProviderStatusButton.Visibility = monitoringEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!monitoringEnabled)
        {
            this.ProviderStatusFlyout.Hide();
            return;
        }

        bool isRefreshing = app.IsProviderStatusRefreshInProgress;
        this.ProviderStatusRefreshButton.IsEnabled = !isRefreshing;
        this.ProviderStatusRefreshGlyph.Visibility = isRefreshing
            ? Visibility.Collapsed
            : Visibility.Visible;
        this.ProviderStatusRefreshRing.IsActive = isRefreshing;
        this.ProviderStatusRefreshRing.Visibility = isRefreshing
            ? Visibility.Visible
            : Visibility.Collapsed;

        ProviderServiceStatusSnapshot[] snapshots = this.Providers
            .Select(provider => app.StatusCoordinator.GetSnapshot(provider.Id))
            .Where(snapshot => snapshot is not null)
            .Cast<ProviderServiceStatusSnapshot>()
            .ToArray();
        int problemCount = snapshots.Count(snapshot => snapshot.HasProblems);
        int failedCount = snapshots.Count(snapshot => snapshot.IsStale);
        DateTimeOffset? latestCheck = snapshots
            .Where(snapshot => snapshot.CheckedAt is not null)
            .Select(snapshot => snapshot.CheckedAt)
            .Max();
        string checkedText = latestCheck is DateTimeOffset checkedAt
            ? UsageText.FormatAge(checkedAt, DateTimeOffset.Now, TimeDisplayPrecision.ThirtySeconds) is string age
                ? age == "just now" ? "Checked just now" : $"Checked {age}"
                : "Checked recently"
            : isRefreshing ? "Checking…" : "Not checked yet";
        string providerCount = this.Providers.Count == 1
            ? "1 enabled provider"
            : $"{this.Providers.Count} enabled providers";
        this.ProviderStatusSummaryText.Text = $"{providerCount} · {checkedText}";

        ProviderStatusVisualLevel visualLevel = problemCount > 0 || failedCount > 0
            ? ProviderStatusVisualLevel.Warning
            : ProviderStatusVisualLevel.Neutral;
        this.ProviderStatusButton.Foreground = (Brush)new ProviderStatusBrushConverter().Convert(
            visualLevel,
            typeof(Brush),
            parameter: null!,
            language: string.Empty);
        this.ProviderStatusButtonGlyph.Glyph = visualLevel == ProviderStatusVisualLevel.Warning
            ? "\uE814"
            : "\uE9D9";
        string buttonLabel = problemCount switch
        {
            1 => "Provider status: 1 provider reports problems",
            > 1 => $"Provider status: {problemCount} providers report problems",
            _ when failedCount > 0 => "Provider status: one or more checks could not refresh",
            _ when isRefreshing => "Provider status: checking enabled providers",
            _ => "Provider status: no problems reported",
        };
        AutomationProperties.SetName(this.ProviderStatusButton, buttonLabel);
        ToolTipService.SetToolTip(this.ProviderStatusButton, buttonLabel);
    }

    private void RefreshUpdatePresentation()
    {
        AppUpdateService updater = ((App)Application.Current).UpdateService;
        this.UpdateActionButton.Visibility = updater.AvailableUpdate is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        this.UpdateActionButton.IsEnabled = !this._isUpdateOperationInProgress;
        if (!this._isUpdateOperationInProgress)
        {
            this.UpdateActionButton.Content = updater.IsUpdateDownloaded
                ? "Restart to update"
                : "Update available";
        }
    }

    private void RefreshVisualStates()
    {
        foreach (ProviderTabViewModel provider in this.Providers)
        {
            provider.RefreshVisualState();
        }
    }

    private async void ProviderTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this.SelectedProvider is null)
        {
            return;
        }

        ProviderTabViewModel selectedProvider = this.SelectedProvider;
        if (this._hasCompletedInitialLoad)
        {
            await this.EnsureSelectedProviderFreshAsync(selectedProvider);
        }
    }

    private void ApplySettings(AppSettings settings, ProviderId? preferredProvider = null)
    {
        Dictionary<ProviderId, ProviderTabViewModel> existingProviders = this.Providers
            .ToDictionary(provider => provider.Id);
        this.Tabs.Clear();
        this.Providers.Clear();
        if (settings.IsAllTabEnabled)
        {
            this.Tabs.Add(this._allProvidersTab);
        }

        foreach (ProviderId id in settings.EnabledProviders)
        {
            if (ProviderId.Supported.Contains(id))
            {
                ProviderTabViewModel provider = existingProviders.GetValueOrDefault(id)
                    ?? new ProviderTabViewModel(id, id.DisplayName);
                this.Providers.Add(provider);
                this.Tabs.Add(provider);
            }
        }

        if (this.Providers.Count == 0)
        {
            ProviderTabViewModel provider = existingProviders.GetValueOrDefault(ProviderId.Codex)
                ?? new ProviderTabViewModel(ProviderId.Codex, ProviderId.Codex.DisplayName);
            this.Providers.Add(provider);
            this.Tabs.Add(provider);
        }

        ProviderId providerToSelect = preferredProvider is ProviderId currentProvider
            && this.Tabs.Any(provider => provider.Id == currentProvider)
                ? currentProvider
                : settings.DefaultProvider;
        this.SelectedProvider = this.Tabs.FirstOrDefault(provider => provider.Id == providerToSelect)
            ?? this.Tabs[0];

        this.OnPropertyChanged(nameof(this.AllSummaryText));
        this.UpdateThemeToggleAppearance();
        this.RefreshProviderStatusPresentation();
    }

    private void UpdateThemeToggleAppearance()
    {
        bool isDark = this.ActualTheme == ElementTheme.Dark;
        this.ThemeToggleGlyph = isDark ? "\uE706" : "\uE708";
        this.ThemeToggleLabel = isDark ? "Switch to light theme" : "Switch to dark theme";
    }

    private async void App_SettingsChanged(AppSettings settings)
    {
        bool codexSparkCardChanged = this._showCodexSparkCard != settings.ShowCodexSparkCard;
        this._showCodexSparkCard = settings.ShowCodexSparkCard;
        bool resetTimeDisplayChanged = this._resetTimeDisplay != settings.ResetTimeDisplay;
        this._resetTimeDisplay = settings.ResetTimeDisplay;
        bool usageValueDisplayChanged = this._usageValueDisplay != settings.UsageValueDisplay;
        this._usageValueDisplay = settings.UsageValueDisplay;
        if (codexSparkCardChanged
            && this.Providers.FirstOrDefault(provider => provider.Id == ProviderId.Codex) is { } codexProvider
            && this._refreshCoordinator.GetSnapshot(ProviderId.Codex) is { } codexSnapshot)
        {
            codexProvider.ApplySnapshot(
                codexSnapshot,
                DateTimeOffset.Now,
                this._timeDisplayPrecision,
                this._showCodexSparkCard,
                this._resetTimeDisplay,
                this._usageValueDisplay);
        }

        TimeSpan refreshInterval = TimeSpan.FromMinutes(settings.RefreshIntervalMinutes);
        bool refreshIntervalChanged = this._refreshTimer.Interval != refreshInterval;
        this._refreshTimer.Interval = refreshInterval;
        if (this.ApplyPresentationCadence(settings.RefreshIntervalMinutes)
            || resetTimeDisplayChanged
            || usageValueDisplayChanged)
        {
            this.UpdatePresentationTime(DateTimeOffset.Now);
        }

        ProviderId[] visibleIds = this.Providers.Select(provider => provider.Id).ToArray();
        bool isAllTabVisible = this.Tabs.Any(provider => provider.IsAll);
        if (visibleIds.SequenceEqual(settings.EnabledProviders)
            && isAllTabVisible == settings.IsAllTabEnabled)
        {
            if (refreshIntervalChanged && this._hasCompletedInitialLoad)
            {
                await this.EnsureSelectedProviderFreshAsync(this.SelectedProvider);
            }

            return;
        }

        ProviderId selectedProvider = this.SelectedProvider.Id;
        this.ApplySettings(settings, selectedProvider);
        await this.EnsureSelectedProviderFreshAsync(this.SelectedProvider);
    }

    private bool ApplyPresentationCadence(int refreshIntervalMinutes)
    {
        PresentationTimeCadence cadence = PresentationTimeCadence.FromRefreshInterval(refreshIntervalMinutes);
        bool precisionChanged = this._timeDisplayPrecision != cadence.Precision;
        if (this._presentationTimer.Interval == cadence.TimerInterval)
        {
            this._timeDisplayPrecision = cadence.Precision;
            return precisionChanged;
        }

        bool restartTimer = this._presentationTimer.IsEnabled;
        if (restartTimer)
        {
            this._presentationTimer.Stop();
        }

        this._presentationTimer.Interval = cadence.TimerInterval;
        this._timeDisplayPrecision = cadence.Precision;
        if (restartTimer)
        {
            this._presentationTimer.Start();
        }

        return precisionChanged;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
