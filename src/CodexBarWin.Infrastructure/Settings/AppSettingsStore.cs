using System.Text.Json;
using System.Text.Json.Serialization;
using CodexBarWin.Core.Providers;

namespace CodexBarWin.Infrastructure.Settings;

public enum AppThemePreference
{
    System,
    Light,
    Dark,
}

public enum ResetTimeDisplayMode
{
    Countdown,
    ExactDateTime,
}

public enum UsageValueDisplayMode
{
    Used,
    Remaining,
}

public enum ApiKeyStorageMode
{
    WindowsCredentialManager,
    EnvironmentVariable,
    SessionOnly,
}

public enum ZaiApiRegion
{
    Global,
    BigModelChina,
}

public enum OpenCodeGoUsageRange
{
    OneDay,
    SevenDays,
    ThirtyDays,
}

public sealed record AppSettings(
    IReadOnlyList<ProviderId> EnabledProviders,
    ProviderId DefaultProvider,
    AppThemePreference Theme = AppThemePreference.System,
    int RefreshIntervalMinutes = 5,
    bool UseTranslucentBackground = false,
    bool IsAllTabEnabled = true,
    ApiKeyStorageMode ZaiApiKeyStorage = ApiKeyStorageMode.WindowsCredentialManager,
    ZaiApiRegion ZaiRegion = ZaiApiRegion.Global,
    bool IsStatusMonitoringEnabled = true,
    bool ShowCodexSparkCard = true,
    ResetTimeDisplayMode ResetTimeDisplay = ResetTimeDisplayMode.Countdown,
    UsageValueDisplayMode UsageValueDisplay = UsageValueDisplayMode.Used,
    ApiKeyStorageMode OpenCodeGoApiKeyStorage = ApiKeyStorageMode.WindowsCredentialManager,
    OpenCodeGoUsageRange OpenCodeGoUsageRange = OpenCodeGoUsageRange.ThirtyDays)
{
    public static AppSettings Default { get; } = new([ProviderId.Codex, ProviderId.Claude], ProviderId.Codex);
}

public sealed record AppSettingsLoadResult(AppSettings Settings, string? SafeWarning = null);

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;

    public AppSettingsStore(string? path = null)
    {
        this._path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexBarWin",
            "settings.json");
    }

    public AppSettingsLoadResult Load()
    {
        if (!File.Exists(this._path))
        {
            return new AppSettingsLoadResult(AppSettings.Default);
        }

        try
        {
            using FileStream stream = File.OpenRead(this._path);
            SettingsDocument? document = JsonSerializer.Deserialize<SettingsDocument>(stream, JsonOptions);
            if (document?.EnabledProviders is null || document.EnabledProviders.Length == 0)
            {
                return new AppSettingsLoadResult(AppSettings.Default, "Saved provider settings were invalid, so defaults were restored.");
            }

            ProviderId[] savedEnabled = document.EnabledProviders
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => new ProviderId(value!))
                .Distinct()
                .ToArray();
            ProviderId[] enabled = savedEnabled
                .Where(IsSupportedProvider)
                .ToArray();
            if (enabled.Length == 0)
            {
                return new AppSettingsLoadResult(
                    AppSettings.Default,
                    "Saved provider settings were unsupported, so defaults were restored.");
            }

            string? savedDefaultProvider = string.IsNullOrWhiteSpace(document.DefaultProvider)
                ? document.SelectedProvider
                : document.DefaultProvider;
            ProviderId defaultProvider = string.IsNullOrWhiteSpace(savedDefaultProvider)
                ? enabled[0]
                : new ProviderId(savedDefaultProvider);
            bool isAllTabEnabled = document.IsAllTabEnabled ?? true;
            if ((defaultProvider == ProviderId.All && !isAllTabEnabled)
                || (defaultProvider != ProviderId.All && !enabled.Contains(defaultProvider)))
            {
                defaultProvider = enabled[0];
            }

            AppThemePreference theme = Enum.TryParse(document.Theme, ignoreCase: true, out AppThemePreference parsedTheme)
                && Enum.IsDefined(parsedTheme)
                    ? parsedTheme
                    : AppThemePreference.System;
            int refreshInterval = IsSupportedRefreshInterval(document.RefreshIntervalMinutes)
                ? document.RefreshIntervalMinutes!.Value
                : AppSettings.Default.RefreshIntervalMinutes;
            ApiKeyStorageMode zaiApiKeyStorage = Enum.TryParse(
                document.ZaiApiKeyStorage,
                ignoreCase: true,
                out ApiKeyStorageMode parsedStorage)
                && Enum.IsDefined(parsedStorage)
                    ? parsedStorage
                    : ApiKeyStorageMode.WindowsCredentialManager;
            ZaiApiRegion zaiRegion = Enum.TryParse(
                document.ZaiRegion,
                ignoreCase: true,
                out ZaiApiRegion parsedRegion)
                && Enum.IsDefined(parsedRegion)
                    ? parsedRegion
                    : ZaiApiRegion.Global;
            ResetTimeDisplayMode resetTimeDisplay = Enum.TryParse(
                document.ResetTimeDisplay,
                ignoreCase: true,
                out ResetTimeDisplayMode parsedResetTimeDisplay)
                && Enum.IsDefined(parsedResetTimeDisplay)
                    ? parsedResetTimeDisplay
                    : ResetTimeDisplayMode.Countdown;
            UsageValueDisplayMode usageValueDisplay = Enum.TryParse(
                document.UsageValueDisplay,
                ignoreCase: true,
                out UsageValueDisplayMode parsedUsageValueDisplay)
                && Enum.IsDefined(parsedUsageValueDisplay)
                    ? parsedUsageValueDisplay
                    : UsageValueDisplayMode.Used;
            ApiKeyStorageMode openCodeGoApiKeyStorage = Enum.TryParse(
                document.OpenCodeGoApiKeyStorage,
                ignoreCase: true,
                out ApiKeyStorageMode parsedOpenCodeGoStorage)
                && Enum.IsDefined(parsedOpenCodeGoStorage)
                    ? parsedOpenCodeGoStorage
                    : ApiKeyStorageMode.WindowsCredentialManager;
            OpenCodeGoUsageRange openCodeGoUsageRange = Enum.TryParse(
                document.OpenCodeGoUsageRange,
                ignoreCase: true,
                out OpenCodeGoUsageRange parsedOpenCodeGoUsageRange)
                && Enum.IsDefined(parsedOpenCodeGoUsageRange)
                    ? parsedOpenCodeGoUsageRange
                    : OpenCodeGoUsageRange.ThirtyDays;

            string? warning = savedEnabled.Length == enabled.Length
                ? null
                : "Unsupported saved providers were removed.";
            return new AppSettingsLoadResult(
                new AppSettings(
                    enabled,
                    defaultProvider,
                    theme,
                    refreshInterval,
                    document.UseTranslucentBackground,
                    isAllTabEnabled,
                    zaiApiKeyStorage,
                    zaiRegion,
                    document.IsStatusMonitoringEnabled ?? true,
                    document.ShowCodexSparkCard ?? true,
                    resetTimeDisplay,
                    usageValueDisplay,
                    openCodeGoApiKeyStorage,
                    openCodeGoUsageRange),
                warning);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new AppSettingsLoadResult(AppSettings.Default, "Saved settings could not be read, so defaults were restored.");
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.EnabledProviders.Count == 0)
        {
            throw new ArgumentException("At least one provider must remain enabled.", nameof(settings));
        }

        if (settings.EnabledProviders.Any(provider => !IsSupportedProvider(provider)))
        {
            throw new ArgumentException("Settings contain an unsupported provider.", nameof(settings));
        }

        if (settings.DefaultProvider == ProviderId.All && !settings.IsAllTabEnabled)
        {
            throw new ArgumentException("The All tab cannot be the default when it is disabled.", nameof(settings));
        }

        if (settings.DefaultProvider != ProviderId.All
            && !settings.EnabledProviders.Contains(settings.DefaultProvider))
        {
            throw new ArgumentException("The default provider must be enabled.", nameof(settings));
        }
        if (!IsSupportedRefreshInterval(settings.RefreshIntervalMinutes))
        {
            throw new ArgumentException("The refresh interval is not supported.", nameof(settings));
        }

        string? directory = Path.GetDirectoryName(this._path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("The settings path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = this._path + ".tmp";
        SettingsDocument document = new(
            settings.EnabledProviders.Select(id => id.Value).ToArray(),
            settings.DefaultProvider.Value,
            settings.Theme.ToString(),
            settings.RefreshIntervalMinutes,
            settings.UseTranslucentBackground,
            settings.IsAllTabEnabled,
            settings.ZaiApiKeyStorage.ToString(),
            settings.ZaiRegion.ToString(),
            settings.IsStatusMonitoringEnabled,
            settings.ShowCodexSparkCard,
            settings.ResetTimeDisplay.ToString(),
            settings.UsageValueDisplay.ToString(),
            settings.OpenCodeGoApiKeyStorage.ToString(),
            settings.OpenCodeGoUsageRange.ToString());

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, this._path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsSupportedRefreshInterval(int? value) => value is 1 or 5 or 15 or 30;

    private static bool IsSupportedProvider(ProviderId providerId) =>
        ProviderId.Supported.Contains(providerId);

    private sealed record SettingsDocument(
        string[] EnabledProviders,
        string? DefaultProvider = null,
        string? Theme = null,
        int? RefreshIntervalMinutes = null,
        bool UseTranslucentBackground = false,
        bool? IsAllTabEnabled = null,
        string? ZaiApiKeyStorage = null,
        string? ZaiRegion = null,
        bool? IsStatusMonitoringEnabled = null,
        bool? ShowCodexSparkCard = null,
        string? ResetTimeDisplay = null,
        string? UsageValueDisplay = null,
        string? OpenCodeGoApiKeyStorage = null,
        string? OpenCodeGoUsageRange = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SelectedProvider = null);
}
