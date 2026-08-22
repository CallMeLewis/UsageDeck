using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.Infrastructure.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "UsageDeck.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultPathUsesPersistentDataDirectoryOutsidePackageRoot()
    {
        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string expected = Path.Combine(localApplicationData, "UsageDeckData", "settings.json");
        string packageRoot = Path.Combine(localApplicationData, "UsageDeck") + Path.DirectorySeparatorChar;

        Assert.Equal(expected, AppSettingsStore.DefaultPath);
        Assert.False(AppSettingsStore.DefaultPath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LoadMigratesLegacySettingsAndRetainsLegacyFile()
    {
        string currentPath = Path.Combine(this._directory, "current", "settings.json");
        string legacyPath = Path.Combine(this._directory, "legacy", "settings.json");
        WriteSettings(legacyPath, "Dark");
        string legacyJson = File.ReadAllText(legacyPath);

        AppSettingsLoadResult result = new AppSettingsStore(currentPath, legacyPath: legacyPath).Load();

        Assert.Equal(AppThemePreference.Dark, result.Settings.Theme);
        Assert.False(result.IsFirstRun);
        Assert.Null(result.SafeWarning);
        Assert.Equal(legacyJson, File.ReadAllText(currentPath));
        Assert.Equal(legacyJson, File.ReadAllText(legacyPath));
    }

    [Fact]
    public void LoadPrefersCurrentSettingsWhenLegacySettingsAlsoExist()
    {
        string currentPath = Path.Combine(this._directory, "current", "settings.json");
        string legacyPath = Path.Combine(this._directory, "legacy", "settings.json");
        WriteSettings(currentPath, "Light");
        WriteSettings(legacyPath, "Dark");

        AppSettingsLoadResult result = new AppSettingsStore(currentPath, legacyPath: legacyPath).Load();

        Assert.Equal(AppThemePreference.Light, result.Settings.Theme);
        Assert.Equal(AppThemePreference.Dark, new AppSettingsStore(legacyPath).Load().Settings.Theme);
    }

    [Fact]
    public void LoadTreatsMissingCurrentAndLegacySettingsAsFirstRun()
    {
        string currentPath = Path.Combine(this._directory, "current", "settings.json");
        string legacyPath = Path.Combine(this._directory, "legacy", "settings.json");

        AppSettingsLoadResult result = new AppSettingsStore(currentPath, legacyPath: legacyPath).Load();

        Assert.Equal(AppSettings.Default, result.Settings);
        Assert.True(result.IsFirstRun);
    }

    [Fact]
    public void LoadUsesLegacySettingsWhenMigrationFails()
    {
        Directory.CreateDirectory(this._directory);
        string blockedDirectory = Path.Combine(this._directory, "blocked");
        File.WriteAllText(blockedDirectory, "This file prevents creation of the destination directory.");
        string currentPath = Path.Combine(blockedDirectory, "settings.json");
        string legacyPath = Path.Combine(this._directory, "legacy", "settings.json");
        WriteSettings(legacyPath, "Dark");

        AppSettingsLoadResult result = new AppSettingsStore(currentPath, legacyPath: legacyPath).Load();

        Assert.Equal(AppThemePreference.Dark, result.Settings.Theme);
        Assert.False(result.IsFirstRun);
        Assert.Contains("could not move", result.SafeWarning, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(legacyPath));
    }

    [Fact]
    public async Task SaveAfterMigrationWritesCurrentSettingsAndRetainsLegacySettings()
    {
        string currentPath = Path.Combine(this._directory, "current", "settings.json");
        string legacyPath = Path.Combine(this._directory, "legacy", "settings.json");
        WriteSettings(legacyPath, "Dark");
        string legacyJson = File.ReadAllText(legacyPath);
        AppSettingsStore store = new(currentPath, legacyPath: legacyPath);
        AppSettings migrated = store.Load().Settings;

        await store.SaveAsync(migrated with { Theme = AppThemePreference.Light });

        Assert.Equal(AppThemePreference.Light, store.Load().Settings.Theme);
        Assert.Equal(legacyJson, File.ReadAllText(legacyPath));
    }

    [Fact]
    public async Task SaveAndLoadRoundTripsAllPersistedSettings()
    {
        DateTimeOffset pauseDeadline = new(2026, 8, 7, 9, 0, 0, TimeSpan.Zero);
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings expected = (AppSettings.Default with
            {
                EnabledProviders = [ProviderId.Claude, ProviderId.Amp, ProviderId.Zai, ProviderId.TheClawBay],
                DefaultProvider = ProviderId.Amp,
                Theme = AppThemePreference.Dark,
                RefreshIntervalMinutes = 15,
                UseTranslucentBackground = true,
                IsAllTabEnabled = false,
                ZaiApiKeyStorage = ApiKeyStorageMode.SessionOnly,
                ZaiRegion = ZaiApiRegion.BigModelChina,
                IsStatusMonitoringEnabled = false,
                ShowCodexSparkCard = false,
                ResetTimeDisplay = ResetTimeDisplayMode.ExactDateTime,
                UsageValueDisplay = UsageValueDisplayMode.Remaining,
                OpenCodeGoApiKeyStorage = ApiKeyStorageMode.SessionOnly,
                OpenCodeGoUsageRange = OpenCodeGoUsageRange.SevenDays,
                CheckForUpdatesAutomatically = false,
                UpdateChannel = AppUpdateChannel.Beta,
                AreNotificationsEnabled = false,
                TheClawBayUsageSource = TheClawBayUsageSource.ApiKey,
                TheClawBayApiKeyStorage = ApiKeyStorageMode.SessionOnly,
                StartAtSignIn = true,
                NotificationsPausedUntilUtc = pauseDeadline,
            })
            .WithProviderNotifications(new ProviderNotificationSettings(
                ProviderId.Codex,
                LimitNotificationThresholds.Remaining10,
                NotifyLimitResets: false,
                NotifyResetCredits: false,
                NotifyStatusChanges: false,
                NotifyConnectionChanges: false));

        await store.SaveAsync(expected);
        AppSettingsLoadResult actual = store.Load();

        Assert.Null(actual.SafeWarning);
        Assert.False(actual.IsFirstRun);
        Assert.Equal(expected.EnabledProviders, actual.Settings.EnabledProviders);
        Assert.Equal(expected.DefaultProvider, actual.Settings.DefaultProvider);
        Assert.Equal(expected.Theme, actual.Settings.Theme);
        Assert.Equal(expected.RefreshIntervalMinutes, actual.Settings.RefreshIntervalMinutes);
        Assert.Equal(expected.UseTranslucentBackground, actual.Settings.UseTranslucentBackground);
        Assert.Equal(expected.IsAllTabEnabled, actual.Settings.IsAllTabEnabled);
        Assert.Equal(expected.ZaiApiKeyStorage, actual.Settings.ZaiApiKeyStorage);
        Assert.Equal(expected.ZaiRegion, actual.Settings.ZaiRegion);
        Assert.Equal(expected.IsStatusMonitoringEnabled, actual.Settings.IsStatusMonitoringEnabled);
        Assert.Equal(expected.ShowCodexSparkCard, actual.Settings.ShowCodexSparkCard);
        Assert.Equal(expected.ResetTimeDisplay, actual.Settings.ResetTimeDisplay);
        Assert.Equal(expected.UsageValueDisplay, actual.Settings.UsageValueDisplay);
        Assert.Equal(expected.OpenCodeGoApiKeyStorage, actual.Settings.OpenCodeGoApiKeyStorage);
        Assert.Equal(expected.OpenCodeGoUsageRange, actual.Settings.OpenCodeGoUsageRange);
        Assert.Equal(expected.CheckForUpdatesAutomatically, actual.Settings.CheckForUpdatesAutomatically);
        Assert.Equal(expected.UpdateChannel, actual.Settings.UpdateChannel);
        Assert.Equal(expected.AreNotificationsEnabled, actual.Settings.AreNotificationsEnabled);
        Assert.Equal(TheClawBayUsageSource.ApiKey, actual.Settings.TheClawBayUsageSource);
        Assert.Equal(ApiKeyStorageMode.SessionOnly, actual.Settings.TheClawBayApiKeyStorage);
        Assert.Equal(expected.StartAtSignIn, actual.Settings.StartAtSignIn);
        Assert.Equal(pauseDeadline, actual.Settings.NotificationsPausedUntilUtc);
        Assert.Equal(
            expected.ProviderNotifications!.ToArray(),
            actual.Settings.ProviderNotifications!.ToArray());

        string savedJson = await File.ReadAllTextAsync(Path.Combine(this._directory, "settings.json"));
        Assert.Contains("\"defaultProvider\": \"amp\"", savedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"selectedProvider\"", savedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveNeverPersistsCredentialFields()
    {
        string path = Path.Combine(this._directory, "settings.json");
        AppSettingsStore store = new(path);

        await store.SaveAsync(AppSettings.Default with
        {
            ZaiApiKeyStorage = ApiKeyStorageMode.SessionOnly,
            OpenCodeGoApiKeyStorage = ApiKeyStorageMode.SessionOnly,
            TheClawBayApiKeyStorage = ApiKeyStorageMode.SessionOnly,
        });

        string savedJson = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("\"zaiApiKey\":", savedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"openCodeGoApiKey\":", savedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"theClawBayApiKey\":", savedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadUsesTheClawBayDefaultsWhenFieldsAreMissing()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["codex"],
              "defaultProvider": "codex"
            }
            """);

        AppSettingsLoadResult result = new AppSettingsStore(path).Load();

        Assert.Equal(TheClawBayUsageSource.Automatic, result.Settings.TheClawBayUsageSource);
        Assert.Equal(ApiKeyStorageMode.WindowsCredentialManager, result.Settings.TheClawBayApiKeyStorage);
    }

    [Fact]
    public void LoadInvalidPreferencesFallBackIndividually()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["codex"],
              "defaultProvider": "codex",
              "theme": "Dark",
              "zaiApiKeyStorage": "PlainText",
              "zaiRegion": "UntrustedEndpoint",
              "resetTimeDisplay": "ProviderDefault",
              "usageValueDisplay": "Both",
              "openCodeGoApiKeyStorage": "PlainText",
              "openCodeGoUsageRange": "NinetyDays",
              "updateChannel": "Nightly",
              "theClawBayUsageSource": "BrowserCookie",
              "theClawBayApiKeyStorage": "PlainText",
              "providerNotifications": [
                {
                  "provider": "codex",
                  "limitNotificationThresholds": "Remaining20, FutureThreshold"
                }
              ]
            }
            """);

        AppSettings settings = new AppSettingsStore(path).Load().Settings;

        Assert.Equal(AppThemePreference.Dark, settings.Theme);
        Assert.Equal(ApiKeyStorageMode.WindowsCredentialManager, settings.ZaiApiKeyStorage);
        Assert.Equal(ZaiApiRegion.Global, settings.ZaiRegion);
        Assert.Equal(ResetTimeDisplayMode.Countdown, settings.ResetTimeDisplay);
        Assert.Equal(UsageValueDisplayMode.Used, settings.UsageValueDisplay);
        Assert.Equal(ApiKeyStorageMode.WindowsCredentialManager, settings.OpenCodeGoApiKeyStorage);
        Assert.Equal(OpenCodeGoUsageRange.ThirtyDays, settings.OpenCodeGoUsageRange);
        Assert.Equal(AppUpdateChannel.Stable, settings.UpdateChannel);
        Assert.Equal(TheClawBayUsageSource.Automatic, settings.TheClawBayUsageSource);
        Assert.Equal(ApiKeyStorageMode.WindowsCredentialManager, settings.TheClawBayApiKeyStorage);
        Assert.Equal(
            AppSettings.Default.GetProviderNotifications(ProviderId.Codex).LimitThresholds,
            settings.GetProviderNotifications(ProviderId.Codex).LimitThresholds);
    }

    [Fact]
    public async Task SaveRejectsUndefinedTheClawBayUsageSource()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings invalid = AppSettings.Default with
        {
            TheClawBayUsageSource = (TheClawBayUsageSource)(-1),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(invalid));
    }

    [Fact]
    public async Task SaveRejectsUndefinedTheClawBayApiKeyStorage()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings invalid = AppSettings.Default with
        {
            TheClawBayApiKeyStorage = (ApiKeyStorageMode)(-1),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(invalid));
    }

    [Fact]
    public void LoadInvalidJsonReturnsSafeDefaults()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, "not-json");

        AppSettingsLoadResult result = new AppSettingsStore(path).Load();

        Assert.Equal(AppSettings.Default, result.Settings);
        Assert.NotNull(result.SafeWarning);
        Assert.False(result.IsFirstRun);
    }

    [Fact]
    public void LoadInvalidJsonPreservesARecoveryCopy()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, "not-json");

        AppSettingsStore store = new(path);
        AppSettingsLoadResult result = store.Load();
        AppSettingsLoadResult repeated = store.Load();

        Assert.NotNull(result.RecoveryPath);
        Assert.Equal("not-json", File.ReadAllText(result.RecoveryPath));
        Assert.Equal(result.RecoveryPath, repeated.RecoveryPath);
        Assert.Single(Directory.GetFiles(this._directory, "settings.json.recovery*"));
    }

    [Fact]
    public void LoadWithoutSavedSettingsUsesConfiguredUpdateChannel()
    {
        string path = Path.Combine(this._directory, "settings.json");
        AppSettingsStore store = new(path, AppUpdateChannel.Beta);

        AppSettingsLoadResult result = store.Load();

        Assert.Equal(AppUpdateChannel.Beta, result.Settings.UpdateChannel);
        Assert.Null(result.SafeWarning);
        Assert.True(result.IsFirstRun);
    }

    [Fact]
    public void LoadSavedUpdateChannelOverridesConfiguredDefault()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["codex"],
              "defaultProvider": "codex",
              "updateChannel": "Stable"
            }
            """);
        AppSettingsStore store = new(path, AppUpdateChannel.Beta);

        AppSettingsLoadResult result = store.Load();

        Assert.Equal(AppUpdateChannel.Stable, result.Settings.UpdateChannel);
    }

    [Fact]
    public void LoadLegacySettingsDefaultsTranslucencyOff()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["codex"],
              "selectedProvider": "codex",
              "theme": "Dark",
              "refreshIntervalMinutes": 5
            }
            """);

        AppSettingsLoadResult result = new AppSettingsStore(path).Load();

        Assert.False(result.Settings.UseTranslucentBackground);
        Assert.True(result.Settings.IsAllTabEnabled);
        Assert.True(result.Settings.IsStatusMonitoringEnabled);
        Assert.True(result.Settings.ShowCodexSparkCard);
        Assert.True(result.Settings.CheckForUpdatesAutomatically);
        Assert.Equal(AppUpdateChannel.Stable, result.Settings.UpdateChannel);
        Assert.Equal(ResetTimeDisplayMode.Countdown, result.Settings.ResetTimeDisplay);
        Assert.Equal(UsageValueDisplayMode.Used, result.Settings.UsageValueDisplay);
        Assert.Equal(ProviderId.Codex, result.Settings.DefaultProvider);
        Assert.Null(result.SafeWarning);
    }

    [Fact]
    public void LoadRemovesUnknownProvidersAndRepairsSelection()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["future-provider", "codex"],
              "selectedProvider": "future-provider",
              "theme": "System",
              "refreshIntervalMinutes": 5
            }
            """);

        AppSettingsLoadResult result = new AppSettingsStore(path).Load();

        Assert.Equal([ProviderId.Codex], result.Settings.EnabledProviders);
        Assert.Equal(ProviderId.Codex, result.Settings.DefaultProvider);
        Assert.NotNull(result.SafeWarning);
    }

    [Fact]
    public void LoadOnlyUnknownProvidersRestoresDefaults()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["future-provider"],
              "selectedProvider": "future-provider"
            }
            """);

        AppSettingsLoadResult result = new AppSettingsStore(path).Load();

        Assert.Equal(AppSettings.Default, result.Settings);
        Assert.NotNull(result.SafeWarning);
    }

    [Fact]
    public void LoadQuietlyDisablesOpenCodeGoAndRepairsSelection()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["opencode-go", "codex"],
              "defaultProvider": "opencode-go"
            }
            """);

        AppSettingsLoadResult result = new AppSettingsStore(path).Load();

        Assert.Equal([ProviderId.Codex], result.Settings.EnabledProviders);
        Assert.Equal(ProviderId.Codex, result.Settings.DefaultProvider);
        Assert.Null(result.SafeWarning);
    }

    [Fact]
    public void LoadOnlyOpenCodeGoQuietlyRestoresAvailableDefaults()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["opencode-go"],
              "defaultProvider": "opencode-go"
            }
            """);

        AppSettingsLoadResult result = new AppSettingsStore(path).Load();

        Assert.Equal(AppSettings.Default, result.Settings);
        Assert.Null(result.SafeWarning);
    }

    [Fact]
    public async Task SaveRejectsDisabledDefaultProvider()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings invalid = new([ProviderId.Codex], ProviderId.Claude);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(invalid));
    }

    [Fact]
    public async Task SaveAndLoadAllowsAllAsDefaultWhenTabIsEnabled()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings expected = AppSettings.Default with { DefaultProvider = ProviderId.All };

        await store.SaveAsync(expected, CancellationToken.None);
        AppSettingsLoadResult actual = store.Load();

        Assert.Equal(ProviderId.All, actual.Settings.DefaultProvider);
        Assert.True(actual.Settings.IsAllTabEnabled);
    }

    [Fact]
    public async Task SaveRejectsAllAsDefaultWhenTabIsDisabled()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings invalid = AppSettings.Default with
        {
            DefaultProvider = ProviderId.All,
            IsAllTabEnabled = false,
        };

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(invalid));
    }

    [Fact]
    public void LoadRepairsDisabledAllDefault()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["codex", "claude"],
              "defaultProvider": "all",
              "isAllTabEnabled": false
            }
            """);

        AppSettingsLoadResult result = new AppSettingsStore(path).Load();

        Assert.False(result.Settings.IsAllTabEnabled);
        Assert.Equal(ProviderId.Codex, result.Settings.DefaultProvider);
    }

    [Fact]
    public async Task SaveRejectsUnsupportedRefreshInterval()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings invalid = AppSettings.Default with { RefreshIntervalMinutes = 3 };

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(invalid));
    }

    [Fact]
    public async Task SaveRejectsUnsupportedProvider()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        ProviderId unsupported = new("future-provider");
        AppSettings invalid = new([unsupported], unsupported);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(invalid));
    }

    [Fact]
    public async Task SaveRejectsUnavailableOpenCodeGoProvider()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings invalid = new([ProviderId.OpenCodeGo], ProviderId.OpenCodeGo);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(invalid));
    }

    [Fact]
    public void LoadSettingsWithoutStartAtSignInDefaultsToOff()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["codex"],
              "defaultProvider": "codex"
            }
            """);

        AppSettings settings = new AppSettingsStore(path).Load().Settings;

        Assert.False(settings.StartAtSignIn);
    }

    [Fact]
    public void LoadLegacyNotificationPreferencesMigratesToEveryProvider()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["codex", "claude"],
              "defaultProvider": "codex",
              "limitNotificationThresholds": "Remaining10",
              "notifyLimitResets": false,
              "notifyCodexResetCredits": false,
              "notifyProviderStatusChanges": false,
              "notifyProviderConnectionChanges": false
            }
            """);

        AppSettings settings = new AppSettingsStore(path).Load().Settings;

        foreach (ProviderId providerId in ProviderId.Supported)
        {
            ProviderNotificationSettings notifications = settings.GetProviderNotifications(providerId);
            Assert.Equal(LimitNotificationThresholds.Remaining10, notifications.LimitThresholds);
            Assert.False(notifications.NotifyLimitResets);
            Assert.False(notifications.NotifyResetCredits);
            Assert.False(notifications.NotifyStatusChanges);
            Assert.False(notifications.NotifyConnectionChanges);
        }
    }

    [Fact]
    public async Task ConcurrentManagerUpdatesPreserveBothChanges()
    {
        string path = Path.Combine(this._directory, "settings.json");
        AppSettingsStore store = new(path);
        AppSettingsManager manager = new(store, AppSettings.Default);

        await Task.WhenAll(
            manager.UpdateAsync(settings => settings with { Theme = AppThemePreference.Dark }),
            manager.UpdateAsync(settings => settings with { RefreshIntervalMinutes = 15 }));

        Assert.Equal(AppThemePreference.Dark, manager.Current.Theme);
        Assert.Equal(15, manager.Current.RefreshIntervalMinutes);
        AppSettingsLoadResult persisted = store.Load();
        Assert.Equal(AppThemePreference.Dark, persisted.Settings.Theme);
        Assert.Equal(15, persisted.Settings.RefreshIntervalMinutes);
    }

    public void Dispose()
    {
        if (Directory.Exists(this._directory))
        {
            Directory.Delete(this._directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static void WriteSettings(string path, string theme)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            {
              "enabledProviders": ["codex"],
              "defaultProvider": "codex",
              "theme": "{{theme}}"
            }
            """);
    }
}
