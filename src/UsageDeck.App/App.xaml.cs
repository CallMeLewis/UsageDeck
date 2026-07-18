using UsageDeck.Core.Providers;
using UsageDeck.Core.Notifications;
using UsageDeck.Infrastructure.Compatibility;
using UsageDeck.Infrastructure.Processes;
using UsageDeck.Infrastructure.Providers.Amp;
using UsageDeck.Infrastructure.Providers.Antigravity;
using UsageDeck.Infrastructure.Providers.Claude;
using UsageDeck.Infrastructure.Providers.Codex;
using UsageDeck.Infrastructure.Providers.Copilot;
using UsageDeck.Infrastructure.Providers.Kiro;
using UsageDeck.Infrastructure.Providers.OpenCodeGo;
using UsageDeck.Infrastructure.Providers.Status;
using UsageDeck.Infrastructure.Providers.Zai;
using UsageDeck.Infrastructure.Security;
using UsageDeck.Infrastructure.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;
using Windows.UI.ViewManagement;

namespace UsageDeck.App;

public partial class App : Application, IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly HttpClient _httpClient;
    private readonly OpenCodeGoApiKeyResolver _openCodeGoApiKeys;
    private readonly NotificationEvaluator _notificationEvaluator = new();
    private readonly WindowsNotificationService _notificationService;
    private readonly SemaphoreSlim _providerStatusRefreshLock = new(1, 1);
    private readonly DispatcherTimer _providerStatusTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly ZaiApiKeyResolver _zaiApiKeys;
    private bool _automaticUpdateChecksEnabled;
    private bool _isDisposed;
    private bool _isHighContrastEnabled;
    private bool _isShuttingDown;
    private AppInstance? _mainInstance;
    private ProviderId[] _monitoredStatusProviders = [];
    private SettingsWindow? _settingsWindow;
    private MainWindow? _window;

    public App()
    {
        this.InitializeComponent();
        this._dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        ExecutableLocator executableLocator = new();
        AppSettingsStore settingsStore = new();
        AppSettingsLoadResult settings = settingsStore.Load();
        this._settingsManager = new AppSettingsManager(settingsStore, settings.Settings);
        this._settingsManager.Changed += this.SettingsManager_Changed;
        this._automaticUpdateChecksEnabled = settings.Settings.CheckForUpdatesAutomatically;
        this._httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        this._zaiApiKeys = new ZaiApiKeyResolver(
            new WindowsCredentialManagerSecretStore(ApplicationIdentity.CredentialTargetPrefix),
            () => this.CurrentSettings.ZaiApiKeyStorage);
        this._openCodeGoApiKeys = new OpenCodeGoApiKeyResolver(
            new WindowsCredentialManagerSecretStore(ApplicationIdentity.CredentialTargetPrefix),
            () => this.CurrentSettings.OpenCodeGoApiKeyStorage);
        this.UpdateService = new AppUpdateService(
            BuildInformation.UpdateRepository,
            settings.Settings.UpdateChannel);
        ProcessSessionFactory processSessionFactory = new();
        PtySessionFactory ptySessionFactory = new();
        CliVersionReader cliVersionReader = new(processSessionFactory);
        IUsageProvider[] providers =
        [
            new CodexUsageProvider(
                processSessionFactory,
                new CodexProcessSpecFactory(new CodexExecutableLocator(executableLocator)),
                ProviderHost.Native,
                cliVersionReader: cliVersionReader),
            new ClaudeUsageProvider(ptySessionFactory, executableLocator, cliVersionReader: cliVersionReader),
            new AntigravityUsageProvider(ptySessionFactory, executableLocator, cliVersionReader: cliVersionReader),
            new CopilotUsageProvider(processSessionFactory, executableLocator, cliVersionReader: cliVersionReader),
            new KiroUsageProvider(
                processSessionFactory,
                ptySessionFactory,
                executableLocator,
                cliVersionReader: cliVersionReader),
            new AmpUsageProvider(processSessionFactory, executableLocator, cliVersionReader: cliVersionReader),
            new OpenCodeGoUsageProvider(
                new OpenCodeGoDataLocator(),
                new OpenCodeGoUsageReader(),
                executableLocator,
                cliVersionReader,
                httpClient: this._httpClient,
                apiKeySource: this._openCodeGoApiKeys,
                usageRange: () => this.CurrentSettings.OpenCodeGoUsageRange),
            new ZaiUsageProvider(
                this._httpClient,
                this._zaiApiKeys,
                () => this.CurrentSettings.ZaiRegion),
        ];

        this.RefreshCoordinator = new ProviderRefreshCoordinator(providers, this._shutdown.Token);
        this.StatusCoordinator = new ProviderStatusCoordinator(
            ProviderStatusSources.Create(this._httpClient),
            this._shutdown.Token);
        this.RefreshCoordinator.SnapshotChanged += this.RefreshCoordinator_SnapshotChanged;
        this.StatusCoordinator.SnapshotChanged += this.StatusCoordinator_SnapshotChanged;
        this._notificationEvaluator.RetainProviders(settings.Settings.EnabledProviders);
        this._notificationService = new WindowsNotificationService();
        this._notificationService.Activated += this.NotificationService_Activated;
        this._notificationService.Initialise();
        this._providerStatusTimer.Tick += this.ProviderStatusTimer_Tick;
    }

    public ProviderRefreshCoordinator RefreshCoordinator { get; }

    public ProviderStatusCoordinator StatusCoordinator { get; }

    internal AppUpdateService UpdateService { get; private set; }

    public static string VersionNumber { get; } = BuildInformation.Version;

    private readonly AppSettingsManager _settingsManager;

    public AppSettings CurrentSettings => this._settingsManager.Current;

    public event Action<AppSettings>? SettingsChanged;

    public event Action? AccessibilityChanged;

    internal event Action? UpdateStateChanged;

    internal event Action? ProviderStatusStateChanged;

    internal bool IsProviderStatusRefreshInProgress { get; private set; }

#if DEBUG
    internal NotificationDeliveryResult TryShowDebugNotification(DebugNotificationScenario scenario)
    {
        UsageNotificationEvent notification = DebugNotificationSamples.Create(scenario);
        NotificationMessage message = NotificationMessageFormatter.Format(
            notification,
            this.CurrentSettings.UsageValueDisplay);
        return this._notificationService.Show(message);
    }
#endif

    internal NotificationDeliveryStatus GetNotificationDeliveryStatus() =>
        this._notificationService.GetStatus();

    internal NotificationDeliveryResult TryShowTestNotification() =>
        this._notificationService.Show(new NotificationMessage(
            "UsageDeck notifications are ready",
            "Windows can show important usage and provider changes.",
            this.CurrentSettings.DefaultProvider));

    internal static bool IsHighContrastEnabled =>
        Current is App app && app._isHighContrastEnabled;

    public Task UpdateSettingsAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken = default) =>
        this._settingsManager.UpdateAsync(update, cancellationToken);

    internal ZaiCredentialStatus GetZaiCredentialStatus() => this._zaiApiKeys.GetStatus();

    internal void SaveZaiApiKey(string apiKey) => this._zaiApiKeys.Save(apiKey);

    internal void DeleteZaiApiKey() => this._zaiApiKeys.Delete();

    internal OpenCodeGoCredentialStatus GetOpenCodeGoCredentialStatus() => this._openCodeGoApiKeys.GetStatus();

    internal void SaveOpenCodeGoApiKey(string apiKey) => this._openCodeGoApiKeys.Save(apiKey);

    internal void DeleteOpenCodeGoApiKey() => this._openCodeGoApiKeys.Delete();

    public void ShowSettingsWindow()
    {
        this._settingsWindow ??= new SettingsWindow();
        this._settingsWindow.RefreshProviderVersions();
        this._settingsWindow.AppWindow.Show();
        this._settingsWindow.Activate();
    }

    public static ElementTheme ToElementTheme(AppThemePreference preference) => preference switch
    {
        AppThemePreference.Light => ElementTheme.Light,
        AppThemePreference.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public static void ApplyWindowAppearance(
        Window window,
        FrameworkElement themedRoot,
        UIElement solidBackground,
        AppSettings settings)
    {
        themedRoot.RequestedTheme = ToElementTheme(settings.Theme);
        if (settings.UseTranslucentBackground && !IsHighContrastEnabled)
        {
            window.SystemBackdrop ??= new MicaBackdrop();
            solidBackground.Visibility = Visibility.Collapsed;
            return;
        }

        window.SystemBackdrop = null;
        solidBackground.Visibility = Visibility.Visible;
    }

    public static void ApplyCaptionButtonColours(Window window, FrameworkElement themedRoot)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        Windows.UI.Color foreground = IsHighContrastEnabled
            ? new UISettings().GetColorValue(UIColorType.Foreground)
            : themedRoot.ActualTheme == ElementTheme.Dark
                ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                : Windows.UI.Color.FromArgb(255, 0, 0, 0);

        window.AppWindow.TitleBar.ButtonForegroundColor = foreground;
        window.AppWindow.TitleBar.ButtonInactiveForegroundColor =
            Windows.UI.Color.FromArgb(153, foreground.R, foreground.G, foreground.B);
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppInstance mainInstance = AppInstance.FindOrRegisterForKey(ApplicationIdentity.MainInstanceKey);
        if (!mainInstance.IsCurrent)
        {
            AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            try
            {
                await mainInstance.RedirectActivationToAsync(activation);
            }
            finally
            {
                Environment.Exit(0);
            }

            return;
        }

        this._mainInstance = mainInstance;
        this._mainInstance.Activated += this.MainInstance_Activated;
        this.ShowMainWindow();
        this.ConfigureProviderStatusMonitoring(this.CurrentSettings, forceRefresh: true);
        if (this.CurrentSettings.CheckForUpdatesAutomatically)
        {
            _ = this.CheckForAppUpdateInBackgroundAsync();
        }
    }

    internal async Task RefreshProviderStatusesAsync(CancellationToken cancellationToken = default)
    {
        if (!this.CurrentSettings.IsStatusMonitoringEnabled)
        {
            return;
        }

        await this._providerStatusRefreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!this.CurrentSettings.IsStatusMonitoringEnabled)
            {
                return;
            }

            this.IsProviderStatusRefreshInProgress = true;
            this.NotifyProviderStatusStateChanged();
            await this.StatusCoordinator.RefreshAsync(
                this.CurrentSettings.EnabledProviders,
                cancellationToken);
        }
        finally
        {
            this.IsProviderStatusRefreshInProgress = false;
            this.NotifyProviderStatusStateChanged();
            this._providerStatusRefreshLock.Release();
        }
    }

    private async Task CheckForAppUpdateInBackgroundAsync()
    {
        AppUpdateService updateService = this.UpdateService;
        if (!updateService.CanCheckForUpdates)
        {
            return;
        }

        try
        {
            await updateService.CheckForUpdatesAsync(this._shutdown.Token);
            if (ReferenceEquals(updateService, this.UpdateService))
            {
                this.NotifyUpdateStateChanged();
            }
        }
        catch (OperationCanceledException) when (this._shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (AppUpdateService.IsExpectedFailure(exception))
        {
            // Update availability must never prevent the usage monitor from starting.
        }
    }

    private void MainInstance_Activated(object? sender, AppActivationArguments args) =>
        _ = this._dispatcherQueue.TryEnqueue(() => this.ShowMainWindow());

    private void NotificationService_Activated(ProviderId? providerId) =>
        _ = this._dispatcherQueue.TryEnqueue(() => this.ShowMainWindow(providerId));

    private void RefreshCoordinator_SnapshotChanged(object? sender, ProviderSnapshot snapshot)
    {
        AppSettings settings = this.CurrentSettings;
        IReadOnlyList<UsageNotificationEvent> notifications = this._notificationEvaluator.EvaluateUsage(
            snapshot,
            CreateNotificationOptions(settings));
        this.ShowNotifications(notifications, settings.UsageValueDisplay);
    }

    private void StatusCoordinator_SnapshotChanged(
        object? sender,
        ProviderServiceStatusSnapshot snapshot)
    {
        AppSettings settings = this.CurrentSettings;
        IReadOnlyList<UsageNotificationEvent> notifications = this._notificationEvaluator.EvaluateStatus(
            snapshot,
            CreateNotificationOptions(settings));
        this.ShowNotifications(notifications, settings.UsageValueDisplay);
    }

    private void ShowNotifications(
        IReadOnlyList<UsageNotificationEvent> notifications,
        UsageValueDisplayMode displayMode)
    {
        if (notifications.Count == 0)
        {
            return;
        }

        NotificationMessage[] messages = notifications
            .Select(notification => NotificationMessageFormatter.Format(notification, displayMode))
            .ToArray();
        _ = this._dispatcherQueue.TryEnqueue(() =>
        {
            if (!this.CurrentSettings.AreNotificationsEnabled)
            {
                return;
            }

            foreach (NotificationMessage message in messages)
            {
                _ = this._notificationService.Show(message);
            }
        });
    }

    private static NotificationEvaluationOptions CreateNotificationOptions(AppSettings settings)
    {
        if (!settings.AreNotificationsEnabled)
        {
            return new NotificationEvaluationOptions(
                [],
                notifyLimitResets: false,
                notifyCodexResetCredits: false,
                notifyProviderStatusChanges: false,
                notifyProviderConnectionChanges: false);
        }

        List<int> thresholds = [];
        if (settings.LimitThresholds.HasFlag(LimitNotificationThresholds.Remaining20))
        {
            thresholds.Add(20);
        }

        if (settings.LimitThresholds.HasFlag(LimitNotificationThresholds.Remaining10))
        {
            thresholds.Add(10);
        }

        if (settings.LimitThresholds.HasFlag(LimitNotificationThresholds.Remaining5))
        {
            thresholds.Add(5);
        }

        if (settings.LimitThresholds.HasFlag(LimitNotificationThresholds.Exhausted))
        {
            thresholds.Add(0);
        }

        return new NotificationEvaluationOptions(
            thresholds,
            settings.NotifyLimitResets,
            settings.NotifyCodexResetCredits,
            settings.NotifyProviderStatusChanges,
            settings.NotifyProviderConnectionChanges);
    }

    private void SettingsManager_Changed(AppSettings settings)
    {
        bool automaticUpdateChecksWereEnabled = this._automaticUpdateChecksEnabled;
        this._automaticUpdateChecksEnabled = settings.CheckForUpdatesAutomatically;
        bool updateChannelChanged = this.UpdateService.Channel != settings.UpdateChannel;
        if (updateChannelChanged)
        {
            this.UpdateService = new AppUpdateService(
                BuildInformation.UpdateRepository,
                settings.UpdateChannel);
            this.NotifyUpdateStateChanged();
        }

        this.ConfigureProviderStatusMonitoring(settings);
        this._notificationEvaluator.RetainProviders(settings.EnabledProviders);
        this.SettingsChanged?.Invoke(settings);
        if (settings.CheckForUpdatesAutomatically
            && (!automaticUpdateChecksWereEnabled || updateChannelChanged))
        {
            _ = this.CheckForAppUpdateInBackgroundAsync();
        }
    }

    private void ConfigureProviderStatusMonitoring(AppSettings settings, bool forceRefresh = false)
    {
        ProviderId[] enabledProviders = settings.EnabledProviders.ToArray();
        bool providersChanged = !this._monitoredStatusProviders.SequenceEqual(enabledProviders);
        this._monitoredStatusProviders = enabledProviders;

        if (!settings.IsStatusMonitoringEnabled)
        {
            this._providerStatusTimer.Stop();
            this.NotifyProviderStatusStateChanged();
            return;
        }

        if (!this._providerStatusTimer.IsEnabled)
        {
            this._providerStatusTimer.Start();
            forceRefresh = true;
        }

        if (forceRefresh || providersChanged)
        {
            _ = this.RefreshProviderStatusesInBackgroundAsync();
        }
    }

    private async void ProviderStatusTimer_Tick(object? sender, object e) =>
        await this.RefreshProviderStatusesInBackgroundAsync();

    private async Task RefreshProviderStatusesInBackgroundAsync()
    {
        try
        {
            await this.RefreshProviderStatusesAsync(this._shutdown.Token);
        }
        catch (OperationCanceledException) when (this._shutdown.IsCancellationRequested)
        {
        }
    }

    private void NotifyProviderStatusStateChanged()
    {
        if (this._dispatcherQueue.HasThreadAccess)
        {
            this.ProviderStatusStateChanged?.Invoke();
            return;
        }

        _ = this._dispatcherQueue.TryEnqueue(() => this.ProviderStatusStateChanged?.Invoke());
    }

    internal void SetHighContrastEnabled(bool enabled)
    {
        if (!this._dispatcherQueue.HasThreadAccess)
        {
            _ = this._dispatcherQueue.TryEnqueue(() => this.SetHighContrastEnabled(enabled));
            return;
        }

        if (this._isHighContrastEnabled == enabled)
        {
            return;
        }

        this._isHighContrastEnabled = enabled;
        this._window?.RefreshAppearance(this.CurrentSettings);
        this._settingsWindow?.RefreshAppearance(this.CurrentSettings);
        this.AccessibilityChanged?.Invoke();
    }


    private void ShowMainWindow(ProviderId? providerId = null)
    {
        this._window ??= new MainWindow();
        if (providerId is ProviderId provider)
        {
            this._window.SelectProvider(provider);
        }

        this._window.AppWindow.Show();
        this._window.Activate();
    }

    public void Shutdown()
    {
        if (this._isShuttingDown)
        {
            return;
        }

        this._isShuttingDown = true;
        this._shutdown.Cancel();
        this._providerStatusTimer.Stop();
        this._settingsWindow?.PrepareForShutdown();
        this._window?.PrepareForShutdown();
        this.Exit();
        this.Dispose();
    }

    internal void RestartForUpdate()
    {
        this.UpdateService.PrepareUpdateAndRestart();
        this.Shutdown();
    }

    internal void NotifyUpdateStateChanged() => this.UpdateStateChanged?.Invoke();

    public void Dispose()
    {
        if (this._isDisposed)
        {
            return;
        }

        this._isDisposed = true;
        if (this._mainInstance is not null)
        {
            this._mainInstance.Activated -= this.MainInstance_Activated;
        }

        this.RefreshCoordinator.SnapshotChanged -= this.RefreshCoordinator_SnapshotChanged;
        this.StatusCoordinator.SnapshotChanged -= this.StatusCoordinator_SnapshotChanged;
        this._notificationService.Activated -= this.NotificationService_Activated;
        this._notificationService.Dispose();

        this._settingsManager.Dispose();
        this._openCodeGoApiKeys.Dispose();
        this._zaiApiKeys.Dispose();
        this._httpClient.Dispose();

        this._shutdown.Dispose();
        GC.SuppressFinalize(this);
    }
}
