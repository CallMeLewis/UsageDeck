using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.Infrastructure.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "UsageDeck.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoadRoundTripsEnabledProviders()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings expected = new(
            [ProviderId.Claude, ProviderId.Antigravity, ProviderId.Copilot, ProviderId.Kiro, ProviderId.Amp],
            ProviderId.Antigravity,
            AppThemePreference.Dark,
            15,
            true,
            false);

        await store.SaveAsync(expected, CancellationToken.None);
        AppSettingsLoadResult actual = store.Load();

        Assert.Null(actual.SafeWarning);
        Assert.Equal(expected.DefaultProvider, actual.Settings.DefaultProvider);
        Assert.Equal(expected.EnabledProviders, actual.Settings.EnabledProviders);
        Assert.Equal(expected.Theme, actual.Settings.Theme);
        Assert.Equal(expected.RefreshIntervalMinutes, actual.Settings.RefreshIntervalMinutes);
        Assert.Equal(expected.UseTranslucentBackground, actual.Settings.UseTranslucentBackground);
        Assert.False(actual.Settings.IsAllTabEnabled);
        Assert.Equal(ApiKeyStorageMode.WindowsCredentialManager, actual.Settings.ZaiApiKeyStorage);
        Assert.Equal(ZaiApiRegion.Global, actual.Settings.ZaiRegion);
        Assert.True(actual.Settings.IsStatusMonitoringEnabled);
        Assert.True(actual.Settings.ShowCodexSparkCard);
        Assert.Equal(ResetTimeDisplayMode.Countdown, actual.Settings.ResetTimeDisplay);
        Assert.Equal(UsageValueDisplayMode.Used, actual.Settings.UsageValueDisplay);
        Assert.Equal(ApiKeyStorageMode.WindowsCredentialManager, actual.Settings.OpenCodeGoApiKeyStorage);
        Assert.Equal(OpenCodeGoUsageRange.ThirtyDays, actual.Settings.OpenCodeGoUsageRange);
        Assert.True(actual.Settings.CheckForUpdatesAutomatically);
        Assert.Equal(AppUpdateChannel.Stable, actual.Settings.UpdateChannel);

        string savedJson = await File.ReadAllTextAsync(Path.Combine(this._directory, "settings.json"));
        Assert.Contains("\"defaultProvider\": \"antigravity\"", savedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"selectedProvider\"", savedJson, StringComparison.Ordinal);
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
    public async Task SaveAndLoadRoundTripsNonSecretZaiPreferences()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings expected = AppSettings.Default with
        {
            ZaiApiKeyStorage = ApiKeyStorageMode.SessionOnly,
            ZaiRegion = ZaiApiRegion.BigModelChina,
        };

        await store.SaveAsync(expected);
        AppSettingsLoadResult actual = store.Load();

        Assert.Equal(ApiKeyStorageMode.SessionOnly, actual.Settings.ZaiApiKeyStorage);
        Assert.Equal(ZaiApiRegion.BigModelChina, actual.Settings.ZaiRegion);
        string savedJson = await File.ReadAllTextAsync(Path.Combine(this._directory, "settings.json"));
        Assert.Contains("\"zaiApiKeyStorage\": \"SessionOnly\"", savedJson, StringComparison.Ordinal);
        Assert.Contains("\"zaiRegion\": \"BigModelChina\"", savedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"zaiApiKey\":", savedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAndLoadRoundTripsNonSecretOpenCodePreferences()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings expected = AppSettings.Default with
        {
            OpenCodeGoApiKeyStorage = ApiKeyStorageMode.SessionOnly,
            OpenCodeGoUsageRange = OpenCodeGoUsageRange.SevenDays,
        };

        await store.SaveAsync(expected);
        AppSettingsLoadResult actual = store.Load();

        Assert.Equal(ApiKeyStorageMode.SessionOnly, actual.Settings.OpenCodeGoApiKeyStorage);
        Assert.Equal(OpenCodeGoUsageRange.SevenDays, actual.Settings.OpenCodeGoUsageRange);
        string savedJson = await File.ReadAllTextAsync(Path.Combine(this._directory, "settings.json"));
        Assert.Contains("\"openCodeGoApiKeyStorage\": \"SessionOnly\"", savedJson, StringComparison.Ordinal);
        Assert.Contains("\"openCodeGoUsageRange\": \"SevenDays\"", savedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"openCodeGoApiKey\":", savedJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAndLoadRoundTripsStatusMonitoringPreference()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings expected = AppSettings.Default with { IsStatusMonitoringEnabled = false };

        await store.SaveAsync(expected);
        AppSettingsLoadResult actual = store.Load();

        Assert.False(actual.Settings.IsStatusMonitoringEnabled);
        string savedJson = await File.ReadAllTextAsync(Path.Combine(this._directory, "settings.json"));
        Assert.Contains("\"isStatusMonitoringEnabled\": false", savedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAndLoadRoundTripsCodexSparkCardPreference()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings expected = AppSettings.Default with { ShowCodexSparkCard = false };

        await store.SaveAsync(expected);
        AppSettingsLoadResult actual = store.Load();

        Assert.False(actual.Settings.ShowCodexSparkCard);
        string savedJson = await File.ReadAllTextAsync(Path.Combine(this._directory, "settings.json"));
        Assert.Contains("\"showCodexSparkCard\": false", savedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAndLoadRoundTripsUpdatePreferences()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings expected = AppSettings.Default with
        {
            CheckForUpdatesAutomatically = false,
            UpdateChannel = AppUpdateChannel.Stable,
        };

        await store.SaveAsync(expected);
        AppSettingsLoadResult actual = store.Load();

        Assert.False(actual.Settings.CheckForUpdatesAutomatically);
        Assert.Equal(AppUpdateChannel.Stable, actual.Settings.UpdateChannel);
        string savedJson = await File.ReadAllTextAsync(Path.Combine(this._directory, "settings.json"));
        Assert.Contains("\"checkForUpdatesAutomatically\": false", savedJson, StringComparison.Ordinal);
        Assert.Contains("\"updateChannel\": \"Stable\"", savedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadInvalidUpdateChannelFallsBackToStable()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["codex"],
              "defaultProvider": "codex",
              "updateChannel": "Nightly"
            }
            """);

        AppSettings settings = new AppSettingsStore(path).Load().Settings;

        Assert.Equal(AppUpdateChannel.Stable, settings.UpdateChannel);
    }

    [Fact]
    public async Task SaveAndLoadRoundTripsResetTimeDisplayPreference()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings expected = AppSettings.Default with
        {
            ResetTimeDisplay = ResetTimeDisplayMode.ExactDateTime,
        };

        await store.SaveAsync(expected);
        AppSettingsLoadResult actual = store.Load();

        Assert.Equal(ResetTimeDisplayMode.ExactDateTime, actual.Settings.ResetTimeDisplay);
        string savedJson = await File.ReadAllTextAsync(Path.Combine(this._directory, "settings.json"));
        Assert.Contains("\"resetTimeDisplay\": \"ExactDateTime\"", savedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadInvalidResetTimeDisplayFallsBackToCountdown()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["codex"],
              "defaultProvider": "codex",
              "resetTimeDisplay": "ProviderDefault"
            }
            """);

        AppSettings settings = new AppSettingsStore(path).Load().Settings;

        Assert.Equal(ResetTimeDisplayMode.Countdown, settings.ResetTimeDisplay);
    }

    [Fact]
    public async Task SaveAndLoadRoundTripsUsageValueDisplayPreference()
    {
        AppSettingsStore store = new(Path.Combine(this._directory, "settings.json"));
        AppSettings expected = AppSettings.Default with
        {
            UsageValueDisplay = UsageValueDisplayMode.Remaining,
        };

        await store.SaveAsync(expected);
        AppSettingsLoadResult actual = store.Load();

        Assert.Equal(UsageValueDisplayMode.Remaining, actual.Settings.UsageValueDisplay);
        string savedJson = await File.ReadAllTextAsync(Path.Combine(this._directory, "settings.json"));
        Assert.Contains("\"usageValueDisplay\": \"Remaining\"", savedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadInvalidUsageValueDisplayFallsBackToUsed()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["codex"],
              "defaultProvider": "codex",
              "usageValueDisplay": "Both"
            }
            """);

        AppSettings settings = new AppSettingsStore(path).Load().Settings;

        Assert.Equal(UsageValueDisplayMode.Used, settings.UsageValueDisplay);
    }

    [Fact]
    public void LoadInvalidZaiPreferencesFallsBackSafely()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["codex"],
              "defaultProvider": "codex",
              "zaiApiKeyStorage": "PlainText",
              "zaiRegion": "UntrustedEndpoint"
            }
            """);

        AppSettings settings = new AppSettingsStore(path).Load().Settings;

        Assert.Equal(ApiKeyStorageMode.WindowsCredentialManager, settings.ZaiApiKeyStorage);
        Assert.Equal(ZaiApiRegion.Global, settings.ZaiRegion);
    }

    [Fact]
    public void LoadInvalidOpenCodePreferencesFallsBackSafely()
    {
        Directory.CreateDirectory(this._directory);
        string path = Path.Combine(this._directory, "settings.json");
        File.WriteAllText(path, """
            {
              "enabledProviders": ["opencode-go"],
              "defaultProvider": "opencode-go",
              "openCodeGoApiKeyStorage": "PlainText",
              "openCodeGoUsageRange": "NinetyDays"
            }
            """);

        AppSettings settings = new AppSettingsStore(path).Load().Settings;

        Assert.Equal(ApiKeyStorageMode.WindowsCredentialManager, settings.OpenCodeGoApiKeyStorage);
        Assert.Equal(OpenCodeGoUsageRange.ThirtyDays, settings.OpenCodeGoUsageRange);
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
}
