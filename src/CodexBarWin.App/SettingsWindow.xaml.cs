using System.Globalization;
using System.Runtime.InteropServices;
using CodexBarWin.Core.Formatting;
using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Providers.OpenCodeGo;
using CodexBarWin.Infrastructure.Providers.Zai;
using CodexBarWin.Infrastructure.Security;
using CodexBarWin.Infrastructure.Settings;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexBarWin.App;

public sealed partial class SettingsWindow : Window, IDisposable
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _isApplyingSettings;
    private bool _isDisposed;
    private bool _isExiting;
    private bool _isUpdateOperationInProgress;
    private readonly Dictionary<ProviderId, string> _providerVersions = ProviderId.Supported
        .ToDictionary(provider => provider, _ => "Checking CLI version…");
    private Task? _providerVersionsTask;
    private ProviderId? _selectedProvider;
    private ThemeSettings? _themeSettings;

    public SettingsWindow()
    {
        this.InitializeComponent();
        App app = (App)Application.Current;
        this.InitialiseThemeSettings(app);
        App.ApplyWindowAppearance(this, this.RootLayout, this.SolidBackground, app.CurrentSettings);
        app.ProviderStatusStateChanged += this.App_ProviderStatusStateChanged;
        app.SettingsChanged += this.App_SettingsChanged;
        app.UpdateStateChanged += this.App_UpdateStateChanged;

        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(this.SettingsTitleBar);
        this.RootLayout.ActualThemeChanged += this.RootLayout_ActualThemeChanged;
        App.ApplyCaptionButtonColours(this, this.RootLayout);

        if (this.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        this.AppWindow.SetIcon("Assets/AppIcon.ico");
        WindowSizing.Configure(this, 900, 700, 680, 480);
        this.AppWindow.Changed += this.AppWindow_Changed;
        this.AppWindow.Closing += this.AppWindow_Closing;

        this.SettingsNavigation.SelectedItem = this.SettingsNavigation.MenuItems[0];
        this.VersionText.Text = $"Version {App.VersionNumber}";
        this.LoadSettings(app.CurrentSettings);
        this.RefreshUpdatePresentation();
    }

    private void LoadSettings(AppSettings settings)
    {
        this._isApplyingSettings = true;
        try
        {
            this.AllTabToggle.IsOn = settings.IsAllTabEnabled;
            this.StatusMonitoringToggle.IsOn = settings.IsStatusMonitoringEnabled;
            this.CodexSparkCardToggle.IsOn = settings.ShowCodexSparkCard;
            this.UpdateSelectedProviderEnabledState(settings);
            foreach (ComboBoxItem item in this.DefaultProviderComboBox.Items.OfType<ComboBoxItem>())
            {
                bool isEnabled = item.Tag is string providerValue
                    && (string.Equals(providerValue, ProviderId.All.Value, StringComparison.Ordinal)
                        ? settings.IsAllTabEnabled
                        : settings.EnabledProviders.Contains(new ProviderId(providerValue)));
                item.Visibility = isEnabled ? Visibility.Visible : Visibility.Collapsed;
            }

            this.DefaultProviderComboBox.SelectedItem = this.DefaultProviderComboBox.Items
                .OfType<ComboBoxItem>()
                .First(item => string.Equals(
                    item.Tag?.ToString(),
                    settings.DefaultProvider.Value,
                    StringComparison.Ordinal));
            int availableDefaultTabs = settings.EnabledProviders.Count + (settings.IsAllTabEnabled ? 1 : 0);
            this.DefaultProviderComboBox.IsEnabled = availableDefaultTabs > 1;
            this.ThemeComboBox.SelectedItem = this.ThemeComboBox.Items
                .OfType<ComboBoxItem>()
                .First(item => string.Equals(
                    item.Tag?.ToString(),
                    settings.Theme.ToString(),
                    StringComparison.Ordinal));
            this.TranslucencyToggle.IsOn = settings.UseTranslucentBackground;
            this.ResetTimeDisplayToggle.IsOn = settings.ResetTimeDisplay == ResetTimeDisplayMode.ExactDateTime;
            this.UsageValueDisplayToggle.IsOn = settings.UsageValueDisplay == UsageValueDisplayMode.Remaining;

            this.ZaiApiKeyStorageComboBox.SelectedItem = this.ZaiApiKeyStorageComboBox.Items
                .OfType<ComboBoxItem>()
                .First(item => string.Equals(
                    item.Tag?.ToString(),
                    settings.ZaiApiKeyStorage.ToString(),
                    StringComparison.Ordinal));
            this.ZaiRegionComboBox.SelectedItem = this.ZaiRegionComboBox.Items
                .OfType<ComboBoxItem>()
                .First(item => string.Equals(
                    item.Tag?.ToString(),
                    settings.ZaiRegion.ToString(),
                    StringComparison.Ordinal));
            this.OpenCodeGoApiKeyStorageComboBox.SelectedItem = this.OpenCodeGoApiKeyStorageComboBox.Items
                .OfType<ComboBoxItem>()
                .First(item => string.Equals(
                    item.Tag?.ToString(),
                    settings.OpenCodeGoApiKeyStorage.ToString(),
                    StringComparison.Ordinal));
            this.OpenCodeGoUsageRangeComboBox.SelectedItem = this.OpenCodeGoUsageRangeComboBox.Items
                .OfType<ComboBoxItem>()
                .First(item => string.Equals(
                    item.Tag?.ToString(),
                    settings.OpenCodeGoUsageRange.ToString(),
                    StringComparison.Ordinal));

            this.RefreshIntervalComboBox.SelectedItem = this.RefreshIntervalComboBox.Items
                .OfType<ComboBoxItem>()
                .First(item => string.Equals(
                    item.Tag?.ToString(),
                    settings.RefreshIntervalMinutes.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal));
        }
        finally
        {
            this._isApplyingSettings = false;
        }

        this.RefreshCodexPresentation();
        this.RefreshOpenCodeGoPresentation(settings);
        this.RefreshZaiPresentation(settings);
        this.RefreshSelectedProviderStatus(settings);
    }

    internal void RefreshProviderVersions()
    {
        if (this._providerVersionsTask is { IsCompleted: false })
        {
            return;
        }

        foreach (ProviderId provider in ProviderId.Supported)
        {
            this._providerVersions[provider] = "Checking CLI version…";
        }

        this.UpdateSelectedProviderVersion();
        this._providerVersionsTask = this.RefreshProviderVersionsAsync();
    }

    private async Task RefreshProviderVersionsAsync()
    {
        App app = (App)Application.Current;
        try
        {
            await Task.WhenAll(
                ProviderId.Supported.Select(provider => this.UpdateProviderVersionAsync(app, provider)));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task UpdateProviderVersionAsync(App app, ProviderId providerId)
    {
        string? version = await app.RefreshCoordinator.ReadCliVersionAsync(
            providerId,
            this._lifetimeCancellation.Token);
        this._providerVersions[providerId] = version is null ? "CLI version unavailable" : $"CLI {version}";
        if (this._selectedProvider == providerId)
        {
            this.UpdateSelectedProviderVersion();
        }
    }

    private void SettingsNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string tag = (args.SelectedItemContainer?.Tag as string) ?? "general";
        this.GeneralPanel.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        this.AppearancePanel.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        this.AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "about")
        {
            this.RefreshUpdatePresentation();
        }

        bool isProvider = this.TrySelectProvider(tag);
        this.ProviderPanel.Visibility = isProvider ? Visibility.Visible : Visibility.Collapsed;
        if (!isProvider)
        {
            this._selectedProvider = null;
        }
    }

    private async void RefreshIntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._isApplyingSettings || this.RefreshIntervalComboBox.SelectedItem is not ComboBoxItem item
            || !int.TryParse(item.Tag?.ToString(), out int minutes))
        {
            return;
        }

        await this.SaveSettingsAsync(settings => settings with { RefreshIntervalMinutes = minutes });
    }

    private async void DefaultProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._isApplyingSettings
            || this.DefaultProviderComboBox.SelectedItem is not ComboBoxItem item
            || item.Tag is not string providerValue)
        {
            return;
        }

        ProviderId defaultProvider = new(providerValue);
        await this.SaveSettingsAsync(settings => settings with { DefaultProvider = defaultProvider });
    }

    private async void AllTabToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (this._isApplyingSettings)
        {
            return;
        }

        bool isEnabled = this.AllTabToggle.IsOn;
        await this.SaveSettingsAsync(settings => settings with
        {
            IsAllTabEnabled = isEnabled,
            DefaultProvider = !isEnabled && settings.DefaultProvider == ProviderId.All
                ? settings.EnabledProviders[0]
                : settings.DefaultProvider,
        });
    }

    private async void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._isApplyingSettings
            || this.ThemeComboBox.SelectedItem is not ComboBoxItem item
            || !Enum.TryParse(item.Tag?.ToString(), ignoreCase: true, out AppThemePreference theme))
        {
            return;
        }

        await this.SaveSettingsAsync(settings => settings with { Theme = theme });
    }

    private async void TranslucencyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (this._isApplyingSettings)
        {
            return;
        }

        bool isOn = this.TranslucencyToggle.IsOn;
        await this.SaveSettingsAsync(settings => settings with { UseTranslucentBackground = isOn });
    }

    private async void ResetTimeDisplayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (this._isApplyingSettings)
        {
            return;
        }

        ResetTimeDisplayMode mode = this.ResetTimeDisplayToggle.IsOn
            ? ResetTimeDisplayMode.ExactDateTime
            : ResetTimeDisplayMode.Countdown;
        await this.SaveSettingsAsync(settings => settings with { ResetTimeDisplay = mode });
    }

    private async void UsageValueDisplayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (this._isApplyingSettings)
        {
            return;
        }

        UsageValueDisplayMode mode = this.UsageValueDisplayToggle.IsOn
            ? UsageValueDisplayMode.Remaining
            : UsageValueDisplayMode.Used;
        await this.SaveSettingsAsync(settings => settings with { UsageValueDisplay = mode });
    }

    private async void StatusMonitoringToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (this._isApplyingSettings)
        {
            return;
        }

        await this.SaveSettingsAsync(settings => settings with
        {
            IsStatusMonitoringEnabled = this.StatusMonitoringToggle.IsOn,
        });
    }

    private async void CodexSparkCardToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (this._isApplyingSettings)
        {
            return;
        }

        await this.SaveSettingsAsync(settings => settings with
        {
            ShowCodexSparkCard = this.CodexSparkCardToggle.IsOn,
        });
    }

    private async void SelectedProviderToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (this._isApplyingSettings || this._selectedProvider is not ProviderId selectedProvider)
        {
            return;
        }

        AppSettings current = ((App)Application.Current).CurrentSettings;
        bool isEnabled = this.SelectedProviderToggle.IsOn;
        if (!isEnabled && current.EnabledProviders.Count == 1 && current.EnabledProviders.Contains(selectedProvider))
        {
            this._isApplyingSettings = true;
            this.SelectedProviderToggle.IsOn = true;
            this._isApplyingSettings = false;
            this.ShowMessage("At least one provider must remain enabled.", InfoBarSeverity.Informational);
            return;
        }

        HashSet<ProviderId> enabledProviders = current.EnabledProviders.ToHashSet();
        if (isEnabled)
        {
            enabledProviders.Add(selectedProvider);
        }
        else
        {
            enabledProviders.Remove(selectedProvider);
        }

        ProviderId[] enabled = ProviderId.Supported.Where(enabledProviders.Contains).ToArray();

        await this.SaveSettingsAsync(settings => settings with
        {
            EnabledProviders = enabled,
            DefaultProvider = (settings.DefaultProvider == ProviderId.All && settings.IsAllTabEnabled)
                || enabled.Contains(settings.DefaultProvider)
                ? settings.DefaultProvider
                : enabled[0],
        });
    }

    private bool TrySelectProvider(string tag)
    {
        const string prefix = "provider:";
        if (!tag.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        ProviderId providerId = new(tag[prefix.Length..]);
        if (!ProviderSettingsPresentation.All.TryGetValue(providerId, out ProviderSettingsPresentation? provider))
        {
            return false;
        }

        this._selectedProvider = providerId;
        this.SelectedProviderLogo.ProviderKey = provider.ProviderKey;
        this.SelectedProviderTitle.Text = provider.DisplayName;
        this.SelectedProviderSource.Text = provider.UsageSource;
        this.SelectedProviderAuthentication.Text = provider.AuthenticationSummary;
        this.SelectedProviderVersionRow.Visibility = provider.ShowsVersion
            ? Visibility.Visible
            : Visibility.Collapsed;
        this.UpdateSelectedProviderVersion();
        AppSettings settings = ((App)Application.Current).CurrentSettings;
        this.UpdateSelectedProviderEnabledState(settings);
        this.RefreshCodexPresentation();
        this.RefreshOpenCodeGoPresentation(settings);
        this.RefreshZaiPresentation(settings);
        this.RefreshSelectedProviderStatus(settings);
        return true;
    }

    private void RefreshCodexPresentation() =>
        this.CodexConfigurationPanel.Visibility = this._selectedProvider == ProviderId.Codex
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void RefreshOpenCodeGoPresentation(AppSettings settings)
    {
        bool isOpenCodeGo = this._selectedProvider == ProviderId.OpenCodeGo;
        this.OpenCodeGoConfigurationPanel.Visibility = isOpenCodeGo ? Visibility.Visible : Visibility.Collapsed;
        if (!isOpenCodeGo)
        {
            return;
        }

        bool usesEnvironment = settings.OpenCodeGoApiKeyStorage == ApiKeyStorageMode.EnvironmentVariable;
        this.OpenCodeGoManagedKeyPanel.Visibility = usesEnvironment ? Visibility.Collapsed : Visibility.Visible;
        this.OpenCodeGoEnvironmentPanel.Visibility = usesEnvironment ? Visibility.Visible : Visibility.Collapsed;
        this.SelectedProviderAuthentication.Text = settings.OpenCodeGoApiKeyStorage switch
        {
            ApiKeyStorageMode.WindowsCredentialManager => "Optional OpenCode Console service key stored in Windows Credential Manager",
            ApiKeyStorageMode.EnvironmentVariable => $"Optional OpenCode Console service key read from {OpenCodeGoApiKeyResolver.EnvironmentVariableName}",
            ApiKeyStorageMode.SessionOnly => "Optional OpenCode Console service key held in memory until CodexBar exits",
            _ => "OpenCode Console API-key storage is not configured",
        };
        try
        {
            OpenCodeGoCredentialStatus status = ((App)Application.Current).GetOpenCodeGoCredentialStatus();
            this.OpenCodeGoCredentialStatusText.Text = status.IsConfigured
                ? $"Configured · {status.StorageDescription} · API billing will be used"
                : $"No service key found · {status.StorageDescription} · local history will be used";
            this.SelectedProviderSource.Text = status.IsConfigured
                ? "OpenCode Console API billing export"
                : "Local OpenCode history";
        }
        catch (SecretStoreException exception)
        {
            this.OpenCodeGoCredentialStatusText.Text = exception.SafeMessage;
            this.SelectedProviderSource.Text = "OpenCode usage source unavailable";
        }
    }

    private void RefreshSelectedProviderStatus(AppSettings? settings = null)
    {
        if (this._selectedProvider is not ProviderId selectedProvider)
        {
            return;
        }

        App app = (App)Application.Current;
        AppSettings currentSettings = settings ?? app.CurrentSettings;
        bool providerEnabled = currentSettings.EnabledProviders.Contains(selectedProvider);
        bool monitorThisProvider = currentSettings.IsStatusMonitoringEnabled && providerEnabled;
        Uri? officialStatusUri = app.StatusCoordinator.GetOfficialStatusUri(selectedProvider);
        ProviderServiceStatusSnapshot? snapshot = providerEnabled
            ? app.StatusCoordinator.GetSnapshot(selectedProvider)
            : null;
        ProviderTabViewModel presentation = new(selectedProvider, selectedProvider.DisplayName);
        presentation.ApplyServiceStatus(snapshot, officialStatusUri, monitorThisProvider);

        if (currentSettings.IsStatusMonitoringEnabled && !providerEnabled)
        {
            presentation.ApplyServiceStatus(snapshot, officialStatusUri, monitoringEnabled: false);
            this.SelectedProviderStatusText.Text = "Not monitored";
            this.SelectedProviderStatusDetail.Text = "Enable this provider in the usage window to monitor its service status.";
            this.SelectedProviderStatusDetail.Visibility = Visibility.Visible;
        }
        else
        {
            this.SelectedProviderStatusText.Text = presentation.ServiceStatusText;
            this.SelectedProviderStatusDetail.Text = presentation.ServiceStatusDetail;
            this.SelectedProviderStatusDetail.Visibility = presentation.HasServiceStatusDetail
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        this.SelectedProviderStatusGlyph.Glyph = presentation.ServiceStatusGlyph;
        Brush statusBrush = (Brush)new ProviderStatusBrushConverter().Convert(
            presentation.ServiceStatusVisualLevel,
            typeof(Brush),
            parameter: null!,
            language: string.Empty);
        this.SelectedProviderStatusGlyph.Foreground = statusBrush;
        this.SelectedProviderStatusText.Foreground = statusBrush;

        this.SelectedProviderStatusCheckedText.Text = snapshot?.CheckedAt is DateTimeOffset checkedAt
            ? UsageText.FormatAge(checkedAt, DateTimeOffset.Now, TimeDisplayPrecision.ThirtySeconds) is string age
                ? age == "just now" ? "Checked just now" : $"Checked {age}"
                : "Checked recently"
            : app.IsProviderStatusRefreshInProgress && monitorThisProvider
                ? "Checking now…"
                : string.Empty;
        this.SelectedProviderStatusSourceLink.Tag = officialStatusUri;
        this.SelectedProviderStatusSourceLink.Visibility = officialStatusUri is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        this.SelectedProviderStatusSourceUnavailable.Visibility = officialStatusUri is null
            ? Visibility.Visible
            : Visibility.Collapsed;

    }

    private async void OfficialStatusLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is Uri uri)
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }

    private void RefreshZaiPresentation(AppSettings settings)
    {
        bool isZai = this._selectedProvider == ProviderId.Zai;
        this.ZaiConfigurationPanel.Visibility = isZai ? Visibility.Visible : Visibility.Collapsed;
        if (!isZai)
        {
            return;
        }

        bool usesEnvironment = settings.ZaiApiKeyStorage == ApiKeyStorageMode.EnvironmentVariable;
        this.ZaiManagedKeyPanel.Visibility = usesEnvironment ? Visibility.Collapsed : Visibility.Visible;
        this.ZaiEnvironmentPanel.Visibility = usesEnvironment ? Visibility.Visible : Visibility.Collapsed;
        this.SelectedProviderAuthentication.Text = settings.ZaiApiKeyStorage switch
        {
            ApiKeyStorageMode.WindowsCredentialManager => "Stored locally in Windows Credential Manager on this PC",
            ApiKeyStorageMode.EnvironmentVariable => $"Read from {ZaiApiKeyResolver.EnvironmentVariableName} when usage refreshes",
            ApiKeyStorageMode.SessionOnly => "Held in memory until CodexBar exits",
            _ => "API-key storage is not configured",
        };
        try
        {
            ZaiCredentialStatus status = ((App)Application.Current).GetZaiCredentialStatus();
            this.ZaiCredentialStatusText.Text = status.IsConfigured
                ? $"Configured · {status.StorageDescription}"
                : $"No API key found · {status.StorageDescription}";
        }
        catch (SecretStoreException exception)
        {
            this.ZaiCredentialStatusText.Text = exception.SafeMessage;
        }
    }

    private void UpdateSelectedProviderEnabledState(AppSettings settings)
    {
        if (this._selectedProvider is not ProviderId selectedProvider)
        {
            return;
        }

        bool wasApplyingSettings = this._isApplyingSettings;
        this._isApplyingSettings = true;
        this.SelectedProviderToggle.IsOn = settings.EnabledProviders.Contains(selectedProvider);
        this._isApplyingSettings = wasApplyingSettings;
    }

    private void UpdateSelectedProviderVersion()
    {
        if (this._selectedProvider is not ProviderId selectedProvider)
        {
            return;
        }

        this.SelectedProviderVersion.Text = this._providerVersions.GetValueOrDefault(
            selectedProvider,
            "CLI version unavailable");
    }

    private async void ZaiRegionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._isApplyingSettings
            || this.ZaiRegionComboBox.SelectedItem is not ComboBoxItem item
            || !Enum.TryParse(item.Tag?.ToString(), ignoreCase: false, out ZaiApiRegion region))
        {
            return;
        }

        await this.SaveSettingsAsync(settings => settings with { ZaiRegion = region });
    }

    private async void ZaiApiKeyStorageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._isApplyingSettings
            || this.ZaiApiKeyStorageComboBox.SelectedItem is not ComboBoxItem item
            || !Enum.TryParse(item.Tag?.ToString(), ignoreCase: false, out ApiKeyStorageMode storageMode))
        {
            return;
        }

        await this.SaveSettingsAsync(settings => settings with { ZaiApiKeyStorage = storageMode });
    }

    private async void OpenCodeGoApiKeyStorageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._isApplyingSettings
            || this.OpenCodeGoApiKeyStorageComboBox.SelectedItem is not ComboBoxItem item
            || !Enum.TryParse(item.Tag?.ToString(), ignoreCase: false, out ApiKeyStorageMode storageMode))
        {
            return;
        }

        await this.SaveSettingsAsync(settings => settings with { OpenCodeGoApiKeyStorage = storageMode });
    }

    private async void OpenCodeGoUsageRangeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (this._isApplyingSettings
            || this.OpenCodeGoUsageRangeComboBox.SelectedItem is not ComboBoxItem item
            || !Enum.TryParse(item.Tag?.ToString(), ignoreCase: false, out OpenCodeGoUsageRange range))
        {
            return;
        }

        await this.SaveSettingsAsync(settings => settings with { OpenCodeGoUsageRange = range });
    }

    private void SaveZaiApiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ((App)Application.Current).SaveZaiApiKey(this.ZaiApiKeyBox.Password);
            this.ZaiApiKeyBox.Password = string.Empty;
            this.RefreshZaiPresentation(((App)Application.Current).CurrentSettings);
            this.ShowMessage("The Z.AI API key was saved in the selected location.", InfoBarSeverity.Success);
        }
        catch (SecretStoreException exception)
        {
            this.ShowMessage(exception.SafeMessage, InfoBarSeverity.Error);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            this.ShowMessage(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void RemoveZaiApiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ((App)Application.Current).DeleteZaiApiKey();
            this.ZaiApiKeyBox.Password = string.Empty;
            this.RefreshZaiPresentation(((App)Application.Current).CurrentSettings);
            this.ShowMessage("The Z.AI API key was removed from the selected location.", InfoBarSeverity.Success);
        }
        catch (SecretStoreException exception)
        {
            this.ShowMessage(exception.SafeMessage, InfoBarSeverity.Error);
        }
        catch (InvalidOperationException exception)
        {
            this.ShowMessage(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task SaveSettingsAsync(Func<AppSettings, AppSettings> update)
    {
        try
        {
            await ((App)Application.Current).UpdateSettingsAsync(update);
            this.SettingsInfoBar.IsOpen = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            this.ShowMessage("That setting could not be saved. Please try again.", InfoBarSeverity.Error);
            this.LoadSettings(((App)Application.Current).CurrentSettings);
        }
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        this.SettingsInfoBar.Message = message;
        this.SettingsInfoBar.Severity = severity;
        this.SettingsInfoBar.IsOpen = true;
    }

    private async void UpdateActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (this._isUpdateOperationInProgress)
        {
            return;
        }

        App app = (App)Application.Current;
        AppUpdateService updater = app.UpdateService;
        if (updater.IsUpdateDownloaded)
        {
            try
            {
                app.RestartForUpdate();
            }
            catch (Exception exception) when (AppUpdateService.IsExpectedFailure(exception))
            {
                this.ShowMessage(
                    "The update could not be started. CodexBar is still running on the current version.",
                    InfoBarSeverity.Error);
            }

            return;
        }

        this._isUpdateOperationInProgress = true;
        this.UpdateActionButton.IsEnabled = false;
        this.UpdateProgressBar.Visibility = Visibility.Visible;
        try
        {
            if (updater.AvailableUpdate is null)
            {
                this.UpdateStatusText.Text = "Checking GitHub for updates…";
                this.UpdateProgressBar.IsIndeterminate = true;
                await updater.CheckForUpdatesAsync(this._lifetimeCancellation.Token);
            }
            else
            {
                this.UpdateStatusText.Text = $"Downloading version {updater.AvailableUpdate.Version}…";
                this.UpdateProgressBar.IsIndeterminate = false;
                this.UpdateProgressBar.Value = 0;
                Progress<int> progress = new(value => this.UpdateProgressBar.Value = value);
                await updater.DownloadUpdateAsync(progress, this._lifetimeCancellation.Token);
            }

            app.NotifyUpdateStateChanged();
            this.SettingsInfoBar.IsOpen = false;
        }
        catch (OperationCanceledException) when (this._lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (AppUpdateService.IsExpectedFailure(exception))
        {
            this.ShowMessage(
                updater.AvailableUpdate is null
                    ? "CodexBar could not check for updates. Check your connection and try again."
                    : "The update could not be downloaded. CodexBar is still running on the current version.",
                InfoBarSeverity.Error);
        }
        finally
        {
            this._isUpdateOperationInProgress = false;
            if (!this._isDisposed)
            {
                this.RefreshUpdatePresentation();
            }
        }
    }

    private void App_UpdateStateChanged()
    {
        if (!this._isDisposed)
        {
            this.RefreshUpdatePresentation();
        }
    }

    private void SaveOpenCodeGoApiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ((App)Application.Current).SaveOpenCodeGoApiKey(this.OpenCodeGoApiKeyBox.Password);
            this.OpenCodeGoApiKeyBox.Password = string.Empty;
            this.RefreshOpenCodeGoPresentation(((App)Application.Current).CurrentSettings);
            this.ShowMessage("The OpenCode Console service-account key was saved in the selected location.", InfoBarSeverity.Success);
        }
        catch (SecretStoreException exception)
        {
            this.ShowMessage(exception.SafeMessage, InfoBarSeverity.Error);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            this.ShowMessage(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void RemoveOpenCodeGoApiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ((App)Application.Current).DeleteOpenCodeGoApiKey();
            this.OpenCodeGoApiKeyBox.Password = string.Empty;
            this.RefreshOpenCodeGoPresentation(((App)Application.Current).CurrentSettings);
            this.ShowMessage("The OpenCode Console service-account key was removed from the selected location.", InfoBarSeverity.Success);
        }
        catch (SecretStoreException exception)
        {
            this.ShowMessage(exception.SafeMessage, InfoBarSeverity.Error);
        }
        catch (InvalidOperationException exception)
        {
            this.ShowMessage(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void App_ProviderStatusStateChanged()
    {
        if (!this._isDisposed)
        {
            this.RefreshSelectedProviderStatus();
        }
    }

    private void RefreshUpdatePresentation()
    {
        if (this._isUpdateOperationInProgress)
        {
            return;
        }

        AppUpdateService updater = ((App)Application.Current).UpdateService;
        this.UpdateProgressBar.Visibility = Visibility.Collapsed;
        this.UpdateProgressBar.IsIndeterminate = false;
        this.UpdateActionButton.IsEnabled = updater.CanCheckForUpdates;

        if (!updater.IsConfigured)
        {
            this.UpdateStatusText.Text = "Set a GitHub release repository when packaging to enable automatic updates.";
            this.UpdateActionButton.Content = "Check for updates";
            return;
        }

        if (!updater.CanCheckForUpdates)
        {
            this.UpdateStatusText.Text = "Update checks are available in Velopack release builds.";
            this.UpdateActionButton.Content = "Check for updates";
            return;
        }

        if (updater.IsUpdateDownloaded && updater.AvailableUpdate is AppUpdateAvailability downloaded)
        {
            this.UpdateStatusText.Text = $"Version {downloaded.Version} is ready to install.";
            this.UpdateActionButton.Content = "Restart to update";
            return;
        }

        if (updater.AvailableUpdate is AppUpdateAvailability available)
        {
            this.UpdateStatusText.Text = $"Version {available.Version} is available.";
            this.UpdateActionButton.Content = "Download update";
            return;
        }

        this.UpdateStatusText.Text = updater.HasCheckedForUpdates
            ? "You’re up to date."
            : "Check GitHub Releases for a newer version.";
        this.UpdateActionButton.Content = "Check for updates";
    }

    private void App_SettingsChanged(AppSettings settings)
    {
        App.ApplyWindowAppearance(this, this.RootLayout, this.SolidBackground, settings);
        this.LoadSettings(settings);
    }

    private void RootLayout_ActualThemeChanged(FrameworkElement sender, object args) =>
        App.ApplyCaptionButtonColours(this, this.RootLayout);

    internal void PrepareForShutdown()
    {
        this._isExiting = true;
        if (this._themeSettings is not null)
        {
            this._themeSettings.Changed -= this.ThemeSettings_Changed;
        }

        this.Dispose();
    }

    public void Dispose()
    {
        if (this._isDisposed)
        {
            return;
        }

        this._isDisposed = true;
        if (Application.Current is App app)
        {
            app.ProviderStatusStateChanged -= this.App_ProviderStatusStateChanged;
            app.SettingsChanged -= this.App_SettingsChanged;
            app.UpdateStateChanged -= this.App_UpdateStateChanged;
        }

        this._lifetimeCancellation.Cancel();
        this._lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    internal void RefreshAppearance(AppSettings settings)
    {
        App.ApplyWindowAppearance(this, this.RootLayout, this.SolidBackground, settings);
        App.ApplyCaptionButtonColours(this, this.RootLayout);
    }

    private void InitialiseThemeSettings(App app)
    {
        try
        {
            ThemeSettings themeSettings = ThemeSettings.CreateForWindowId(this.AppWindow.Id);
            themeSettings.Changed += this.ThemeSettings_Changed;
            this._themeSettings = themeSettings;
            app.SetHighContrastEnabled(themeSettings.HighContrast);
        }
        catch (COMException)
        {
            // XAML theme resources still follow Windows high contrast if notifications are unavailable.
        }
    }

    private void ThemeSettings_Changed(ThemeSettings sender, object args) =>
        ((App)Application.Current).SetHighContrastEnabled(sender.HighContrast);

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!this._isExiting)
        {
            args.Cancel = true;
            sender.Hide();
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange)
        {
            WindowSizing.UpdateMinimumSize(this, 680, 480);
        }
    }
}
