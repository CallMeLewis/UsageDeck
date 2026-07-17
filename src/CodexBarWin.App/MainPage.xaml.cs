using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexBarWin.Core.Formatting;
using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace CodexBarWin.App;

public sealed partial class MainPage : Page, INotifyPropertyChanged
{
    private readonly ProviderTabViewModel _allProvidersTab = new(ProviderId.All, ProviderId.All.DisplayName);
    private readonly ProviderRefreshCoordinator _refreshCoordinator;
    private readonly HashSet<ProviderTabViewModel> _providerRefreshesInProgress = [];
    private readonly DispatcherTimer _presentationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly UISettings _uiSettings = new();
    private bool _hasCompletedInitialLoad;
    private bool _hasShownInitialContent;
    private int _refreshOperationsInProgress;
    private Storyboard? _skeletonShimmerStoryboard;
    private ProviderTabViewModel _selectedProvider = null!;
    private TimeDisplayPrecision _timeDisplayPrecision = TimeDisplayPrecision.Seconds;
    private string _themeToggleGlyph = "\uE708";
    private string _themeToggleLabel = "Switch to dark theme";
    private string _appVersionText = $"v{App.VersionNumber}";

    public MainPage()
    {
        this.InitializeComponent();
        this._refreshCoordinator = ((App)Application.Current).RefreshCoordinator;
        App app = (App)Application.Current;
        this._refreshTimer.Interval = TimeSpan.FromMinutes(app.CurrentSettings.RefreshIntervalMinutes);
        this.ApplyPresentationCadence(app.CurrentSettings.RefreshIntervalMinutes);
        app.SettingsChanged += this.App_SettingsChanged;
        app.AccessibilityChanged += this.App_AccessibilityChanged;
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
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProviderTabViewModel> Tabs { get; }

    public ObservableCollection<ProviderTabViewModel> Providers { get; }

    public string AllSummaryText => this.Providers.Count == 1
        ? "1 enabled provider"
        : $"{this.Providers.Count} enabled providers";

    public string AppVersionText
    {
        get => this._appVersionText;
        private set
        {
            if (this._appVersionText == value)
            {
                return;
            }

            this._appVersionText = value;
            this.OnPropertyChanged();
        }
    }

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
        provider.ApplySnapshot(snapshot, DateTimeOffset.Now, this._timeDisplayPrecision);
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
            provider.UpdateTime(now, this._timeDisplayPrecision);
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

    private void MainPage_ActualThemeChanged(FrameworkElement sender, object args)
    {
        this.UpdateThemeToggleAppearance();
        this.RefreshVisualStates();
    }

    private void App_AccessibilityChanged()
    {
        this.RefreshVisualStates();
        if (App.IsHighContrastEnabled)
        {
            this.StopSkeletonShimmer();
            return;
        }

        this.StartSkeletonShimmer();
    }

    private void App_UpdateStateChanged()
    {
        AppUpdateService updater = ((App)Application.Current).UpdateService;
        this.AppVersionText = updater.IsUpdateDownloaded
            ? $"v{App.VersionNumber} · Restart to update"
            : updater.AvailableUpdate is not null
                ? $"v{App.VersionNumber} · Update available"
                : $"v{App.VersionNumber}";
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
    }

    private void UpdateThemeToggleAppearance()
    {
        bool isDark = this.ActualTheme == ElementTheme.Dark;
        this.ThemeToggleGlyph = isDark ? "\uE706" : "\uE708";
        this.ThemeToggleLabel = isDark ? "Switch to light theme" : "Switch to dark theme";
    }

    private async void App_SettingsChanged(AppSettings settings)
    {
        TimeSpan refreshInterval = TimeSpan.FromMinutes(settings.RefreshIntervalMinutes);
        bool refreshIntervalChanged = this._refreshTimer.Interval != refreshInterval;
        this._refreshTimer.Interval = refreshInterval;
        if (this.ApplyPresentationCadence(settings.RefreshIntervalMinutes))
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
