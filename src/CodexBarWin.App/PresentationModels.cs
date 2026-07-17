using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodexBarWin.Core.Formatting;
using CodexBarWin.Core.Providers;
using CodexBarWin.Infrastructure.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace CodexBarWin.App;

public enum ProviderStatusVisualLevel
{
    Neutral,
    Success,
    Warning,
}

public sealed record ProviderSettingsPresentation(
    ProviderId Id,
    string Description,
    string UsageSource,
    string ConnectionLabel,
    string ConnectionValue,
    bool ShowsVersion,
    string AuthenticationSummary,
    string PrivacySummary)
{
    public static IReadOnlyDictionary<ProviderId, ProviderSettingsPresentation> All { get; } =
        new Dictionary<ProviderId, ProviderSettingsPresentation>
        {
            [ProviderId.Codex] = new(
                ProviderId.Codex,
                "Shows Codex plan limits from the local Codex app server.",
                "Codex app server",
                "Command-line tool",
                "codex",
                true,
                "Owned by the Codex CLI",
                "CodexBar uses the provider-owned sign-in and does not store credentials from the Codex CLI."),
            [ProviderId.Claude] = new(
                ProviderId.Claude,
                "Shows subscription quota from the local Claude Code usage screen.",
                "Claude Code CLI",
                "Command-line tool",
                "claude",
                true,
                "Owned by the Claude Code CLI",
                "CodexBar uses the provider-owned sign-in and does not store credentials from the Claude Code CLI."),
            [ProviderId.Antigravity] = new(
                ProviderId.Antigravity,
                "Shows model quota data from the signed-in Antigravity CLI.",
                "Antigravity CLI",
                "Command-line tool",
                "agy",
                true,
                "Owned by the Antigravity CLI",
                "CodexBar uses the provider-owned sign-in and does not store credentials from the Antigravity CLI."),
            [ProviderId.Copilot] = new(
                ProviderId.Copilot,
                "Shows GitHub Copilot quota data through the signed-in GitHub CLI.",
                "GitHub CLI",
                "Command-line tool",
                "gh",
                true,
                "Owned by the GitHub CLI",
                "CodexBar uses the provider-owned sign-in and does not store credentials from the GitHub CLI."),
            [ProviderId.Kiro] = new(
                ProviderId.Kiro,
                "Shows plan allowance and bonus credits from the signed-in Kiro CLI.",
                "Kiro CLI",
                "Command-line tool",
                "kiro-cli",
                true,
                "Owned by the Kiro CLI",
                "CodexBar uses the provider-owned sign-in and does not store credentials from the Kiro CLI."),
            [ProviderId.Amp] = new(
                ProviderId.Amp,
                "Shows Amp Free usage and credit balance from the signed-in Amp CLI.",
                "Amp CLI",
                "Command-line tool",
                "amp",
                true,
                "Owned by the Amp CLI",
                "CodexBar uses the provider-owned sign-in and does not store credentials from the Amp CLI."),
            [ProviderId.OpenCodeGo] = new(
                ProviderId.OpenCodeGo,
                "Shows estimated OpenCode Go plan usage derived from local assistant costs.",
                "Local OpenCode history",
                "Command-line tool",
                "opencode",
                true,
                "Owned by OpenCode; CodexBar does not read its credentials",
                "CodexBar opens opencode.db read-only and reads only timestamps and costs. It never reads auth.json."),
            [ProviderId.Zai] = new(
                ProviderId.Zai,
                "Shows personal Z.AI Coding Plan limits from the selected regional API.",
                "Z.AI Coding Plan API",
                "API endpoint",
                "https://api.z.ai/api/monitor/usage/quota/limit",
                false,
                "Uses the API key storage selected below",
                "CodexBar sends the key only to the fixed Z.AI endpoint for the selected region and never stores raw responses."),
        };

    public string DisplayName => this.Id.DisplayName;

    public string ProviderKey => this.Id.Value;

}

public sealed class ProviderTabViewModel : INotifyPropertyChanged
{
    private DateTimeOffset _capturedAt = DateTimeOffset.MinValue;
    private bool _canRetry;
    private string _cliVersionText = string.Empty;
    private string _creditsText = string.Empty;
    private bool _isLoading = true;
    private bool _isSelected;
    private string _planText = string.Empty;
    private string _resetCreditsNoticeText = string.Empty;
    private string _resetCreditsSummaryText = string.Empty;
    private string _serviceStatusDetail = string.Empty;
    private string _serviceStatusGlyph = "\uE946";
    private string _serviceStatusText = "Checking status…";
    private ProviderStatusVisualLevel _serviceStatusVisualLevel;
    private Uri? _officialStatusUri;
    private bool _hasServiceProblem;
    private string _statusMessage = string.Empty;
    private string _sourceDescription = "No source";
    private string _summaryUpdatedText = "Not refreshed";
    private UsageDataState _state = UsageDataState.Unavailable;
    private string _updatedText = "Not refreshed yet";

    public ProviderTabViewModel(ProviderId id, string displayName)
    {
        this.Id = id;
        this.DisplayName = displayName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProviderId Id { get; }

    public string DisplayName { get; }

    public string ProviderKey => this.Id.Value;

    public bool IsAll => this.Id == ProviderId.All;

    public ObservableCollection<UsageWindowViewModel> UsageWindows { get; } = [];

    public ObservableCollection<RateLimitResetCreditViewModel> ResetCredits { get; } = [];

    public string LimitsSummaryText => this.UsageWindows.Count == 1
        ? "1 active limit"
        : $"{this.UsageWindows.Count} active limits";

    public bool HasUsageWindows => this.UsageWindows.Count > 0;

    public string PlanText
    {
        get => this._planText;
        private set
        {
            if (this.SetField(ref this._planText, value))
            {
                this.OnPropertyChanged(nameof(this.HasPlan));
            }
        }
    }

    public bool HasPlan => !string.IsNullOrEmpty(this.PlanText);

    public string CliVersionText
    {
        get => this._cliVersionText;
        private set
        {
            if (this.SetField(ref this._cliVersionText, value))
            {
                this.OnPropertyChanged(nameof(this.HasCliVersion));
            }
        }
    }

    public bool HasCliVersion => !string.IsNullOrEmpty(this.CliVersionText);

    public string CreditsText
    {
        get => this._creditsText;
        private set
        {
            if (this.SetField(ref this._creditsText, value))
            {
                this.OnPropertyChanged(nameof(this.HasCredits));
                this.OnPropertyChanged(nameof(this.HasNoUsageData));
            }
        }
    }

    public bool HasCredits => !string.IsNullOrEmpty(this.CreditsText);

    public string ResetCreditsSummaryText
    {
        get => this._resetCreditsSummaryText;
        private set
        {
            if (this.SetField(ref this._resetCreditsSummaryText, value))
            {
                this.OnPropertyChanged(nameof(this.HasResetCredits));
            }
        }
    }

    public bool HasResetCredits => !string.IsNullOrEmpty(this.ResetCreditsSummaryText);

    public string ResetCreditsNoticeText
    {
        get => this._resetCreditsNoticeText;
        private set
        {
            if (this.SetField(ref this._resetCreditsNoticeText, value))
            {
                this.OnPropertyChanged(nameof(this.HasResetCreditsNotice));
            }
        }
    }

    public bool HasResetCreditsNotice => !string.IsNullOrEmpty(this.ResetCreditsNoticeText);

    public string UpdatedText
    {
        get => this._updatedText;
        private set => this.SetField(ref this._updatedText, value);
    }

    public string SummaryUpdatedText
    {
        get => this._summaryUpdatedText;
        private set => this.SetField(ref this._summaryUpdatedText, value);
    }

    public string StatusMessage
    {
        get => this._statusMessage;
        private set
        {
            if (this.SetField(ref this._statusMessage, value))
            {
                this.OnPropertyChanged(nameof(this.HasStatusMessage));
                this.OnPropertyChanged(nameof(this.HasNoUsageData));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(this.StatusMessage);

    public string ServiceStatusText
    {
        get => this._serviceStatusText;
        private set => this.SetField(ref this._serviceStatusText, value);
    }

    public string ServiceStatusDetail
    {
        get => this._serviceStatusDetail;
        private set
        {
            if (this.SetField(ref this._serviceStatusDetail, value))
            {
                this.OnPropertyChanged(nameof(this.HasServiceStatusDetail));
            }
        }
    }

    public bool HasServiceStatusDetail => !string.IsNullOrEmpty(this.ServiceStatusDetail);

    public string ServiceStatusGlyph
    {
        get => this._serviceStatusGlyph;
        private set => this.SetField(ref this._serviceStatusGlyph, value);
    }

    public ProviderStatusVisualLevel ServiceStatusVisualLevel
    {
        get => this._serviceStatusVisualLevel;
        private set => this.SetField(ref this._serviceStatusVisualLevel, value);
    }

    public bool HasServiceProblem
    {
        get => this._hasServiceProblem;
        private set => this.SetField(ref this._hasServiceProblem, value);
    }

    public string ServiceProblemTitle => $"{this.DisplayName} reports a service problem";

    public Uri? OfficialStatusUri
    {
        get => this._officialStatusUri;
        private set
        {
            if (this.SetField(ref this._officialStatusUri, value))
            {
                this.OnPropertyChanged(nameof(this.HasOfficialStatusPage));
                this.OnPropertyChanged(nameof(this.ShowServiceStatusLink));
            }
        }
    }

    public bool HasOfficialStatusPage => this.OfficialStatusUri is not null;

    public bool ShowServiceStatusLink => this.HasOfficialStatusPage
        && (this.HasServiceProblem || this.ServiceStatusText == "Couldn’t refresh");

    public string ServiceStatusAccessibleName => this.HasServiceStatusDetail
        ? $"{this.DisplayName}, {this.ServiceStatusText}, {this.ServiceStatusDetail}"
        : $"{this.DisplayName}, {this.ServiceStatusText}";

    public bool HasNoUsageData => this.HasLoaded
        && !this.IsLoading
        && this.UsageWindows.Count == 0
        && !this.HasCredits
        && !this.HasStatusMessage;

    public string EmptyStateTitle => this.Id == ProviderId.OpenCodeGo
        ? "No OpenCode Go usage found"
        : "No rate-limit data reported";

    public string EmptyStateMessage => this.Id == ProviderId.OpenCodeGo
        ? "CodexBar found the local OpenCode database, but it contains no requests billed through OpenCode Go. Local-only stats appear after you use an OpenCode Go model."
        : "This provider did not return any rate-limit windows.";

    public bool CanRetry
    {
        get => this._canRetry;
        private set => this.SetField(ref this._canRetry, value);
    }

    public bool IsLoading
    {
        get => this._isLoading;
        set
        {
            if (this.SetField(ref this._isLoading, value))
            {
                this.OnPropertyChanged(nameof(this.HasNoUsageData));
            }
        }
    }

    public bool HasLoaded { get; private set; }

    public bool NeedsRefresh(DateTimeOffset now, TimeSpan maximumAge)
    {
        if (maximumAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge), "The maximum age cannot be negative.");
        }

        return !this.HasLoaded
            || this._capturedAt == DateTimeOffset.MinValue
            || now - this._capturedAt >= maximumAge;
    }

    public bool IsSelected
    {
        get => this._isSelected;
        set => this.SetField(ref this._isSelected, value);
    }

    public void ApplySnapshot(
        ProviderSnapshot snapshot,
        DateTimeOffset now,
        TimeDisplayPrecision precision,
        bool showCodexSparkCard = true)
    {
        this.HasLoaded = true;
        this.IsLoading = false;
        this._capturedAt = snapshot.CapturedAt;
        this._sourceDescription = snapshot.SourceDescription;
        this._state = snapshot.State;
        this.PlanText = FormatPlanName(snapshot.Identity?.Plan);
        this.CliVersionText = string.IsNullOrWhiteSpace(snapshot.CliVersion)
            ? string.Empty
            : $"CLI {snapshot.CliVersion}";
        this.CreditsText = FormatCredits(snapshot.Credits);
        this.ResetCreditsSummaryText = FormatResetCreditsSummary(snapshot.ResetCredits);
        this.ResetCreditsNoticeText = FormatResetCreditsNotice(snapshot.ResetCredits);
        this.CanRetry = snapshot.SafeError is not null;
        this.StatusMessage = snapshot.SafeError ?? string.Empty;

        this.UsageWindows.Clear();
        foreach (UsageWindow window in snapshot.UsageWindows)
        {
            if (!showCodexSparkCard
                && snapshot.ProviderId == ProviderId.Codex
                && IsCodexSparkWindow(window))
            {
                continue;
            }

            this.UsageWindows.Add(new UsageWindowViewModel(window, now, precision));
        }

        this.ResetCredits.Clear();
        if (snapshot.ResetCredits is not null)
        {
            for (int index = 0; index < snapshot.ResetCredits.Credits.Count; index++)
            {
                this.ResetCredits.Add(new RateLimitResetCreditViewModel(
                    snapshot.ResetCredits.Credits[index],
                    index + 1));
            }
        }

        this.OnPropertyChanged(nameof(this.LimitsSummaryText));
        this.OnPropertyChanged(nameof(this.HasUsageWindows));
        this.OnPropertyChanged(nameof(this.HasNoUsageData));
        this.UpdateTime(now, precision);
    }

    private static bool IsCodexSparkWindow(UsageWindow window) =>
        window.Id.Equals("codex-spark", StringComparison.OrdinalIgnoreCase)
        || window.Id.StartsWith("codex-spark-", StringComparison.OrdinalIgnoreCase)
        || window.DisplayName.Contains("spark", StringComparison.OrdinalIgnoreCase);

    public void ShowWarning(string message)
    {
        this.CanRetry = false;
        this.StatusMessage = message;
    }

    public void ApplyServiceStatus(
        ProviderServiceStatusSnapshot? snapshot,
        Uri? officialStatusUri,
        bool monitoringEnabled)
    {
        this.OfficialStatusUri = snapshot?.IncidentUri ?? snapshot?.OfficialStatusUri ?? officialStatusUri;
        this.HasServiceProblem = false;

        if (!monitoringEnabled)
        {
            this.ServiceStatusText = "Status monitoring off";
            this.ServiceStatusDetail = string.Empty;
            this.ServiceStatusGlyph = "\uE946";
            this.ServiceStatusVisualLevel = ProviderStatusVisualLevel.Neutral;
            this.NotifyServiceStatusAccessibilityChanged();
            return;
        }

        if (snapshot is null)
        {
            this.ServiceStatusText = officialStatusUri is null
                ? "Official status unavailable"
                : "Checking status…";
            this.ServiceStatusDetail = officialStatusUri is null
                ? "This provider does not publish a verified public status source."
                : string.Empty;
            this.ServiceStatusGlyph = officialStatusUri is null ? "\uE946" : "\uE895";
            this.ServiceStatusVisualLevel = ProviderStatusVisualLevel.Neutral;
            this.NotifyServiceStatusAccessibilityChanged();
            return;
        }

        if (snapshot.IsStale && !snapshot.HasProblems)
        {
            this.ServiceStatusText = "Couldn’t refresh";
            this.ServiceStatusDetail = snapshot.SafeError ?? snapshot.Summary;
            this.ServiceStatusGlyph = "\uE814";
            this.ServiceStatusVisualLevel = ProviderStatusVisualLevel.Warning;
            this.NotifyServiceStatusAccessibilityChanged();
            return;
        }

        this.ServiceStatusText = snapshot.Health switch
        {
            ProviderServiceHealth.Operational => "Operational",
            ProviderServiceHealth.ProblemsReported => "Problems reported",
            ProviderServiceHealth.OfficialStatusUnavailable => "Official status unavailable",
            _ => "Couldn’t refresh",
        };
        this.ServiceStatusDetail = snapshot.IsStale
            ? $"{snapshot.Summary} The status refresh failed, so this is the last known result."
            : snapshot.Health == ProviderServiceHealth.Operational
                ? string.Empty
                : snapshot.Summary;
        this.HasServiceProblem = snapshot.HasProblems;
        this.ServiceStatusGlyph = snapshot.Health switch
        {
            ProviderServiceHealth.Operational => "\uE73E",
            ProviderServiceHealth.ProblemsReported => "\uE814",
            _ => "\uE946",
        };
        this.ServiceStatusVisualLevel = snapshot.Health switch
        {
            ProviderServiceHealth.Operational => ProviderStatusVisualLevel.Success,
            ProviderServiceHealth.ProblemsReported => ProviderStatusVisualLevel.Warning,
            _ => ProviderStatusVisualLevel.Neutral,
        };
        this.NotifyServiceStatusAccessibilityChanged();
    }

    public void UpdateTime(DateTimeOffset now, TimeDisplayPrecision precision)
    {
        foreach (UsageWindowViewModel usageWindow in this.UsageWindows)
        {
            usageWindow.UpdateTime(now, precision);
        }

        if (this._capturedAt == DateTimeOffset.MinValue)
        {
            this.SummaryUpdatedText = "No data yet";
            this.UpdatedText = "No data yet";
            return;
        }

        string age = UsageText.FormatAge(this._capturedAt, now, precision);
        string freshness = this._state == UsageDataState.Stale
            ? age == "just now" ? "Showing saved data" : $"Showing saved data from {age}"
            : age == "just now" ? "Updated just now" : $"Updated {age}";
        this.SummaryUpdatedText = this._state == UsageDataState.Stale
            ? age == "just now" ? "Saved just now" : $"Saved {age}"
            : age == "just now" ? "Updated just now" : $"Updated {age}";
        this.UpdatedText = $"{freshness} · {this._sourceDescription}";
    }

    public void RefreshVisualState()
    {
        foreach (UsageWindowViewModel usageWindow in this.UsageWindows)
        {
            usageWindow.RefreshVisualState();
        }

        this.OnPropertyChanged(nameof(this.ServiceStatusVisualLevel));
    }

    private void NotifyServiceStatusAccessibilityChanged()
    {
        this.OnPropertyChanged(nameof(this.ServiceStatusAccessibleName));
        this.OnPropertyChanged(nameof(this.ServiceProblemTitle));
        this.OnPropertyChanged(nameof(this.ShowServiceStatusLink));
    }

    private static string FormatPlanName(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
        {
            return string.Empty;
        }

        return plan.Trim().ToLowerInvariant() switch
        {
            "prolite" or "pro-lite" or "pro_lite" => "Pro Lite",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
                plan.Trim().Replace('-', ' ').Replace('_', ' ').ToLowerInvariant()),
        };
    }

    private static string FormatCredits(CreditBalance? credits)
    {
        if (credits is null || (!credits.HasCredits && !credits.IsUnlimited))
        {
            return string.Empty;
        }

        if (credits.IsUnlimited)
        {
            return "Unlimited";
        }

        return string.IsNullOrWhiteSpace(credits.Balance)
            ? "Available"
            : credits.Balance.Trim();
    }

    private static string FormatResetCreditsSummary(RateLimitResetCredits? resetCredits) =>
        resetCredits?.AvailableCount switch
        {
            null => string.Empty,
            1 => "1 available",
            var count => $"{count} available",
        };

    private static string FormatResetCreditsNotice(RateLimitResetCredits? resetCredits)
    {
        if (resetCredits is null
            || resetCredits.AvailableCount == 0
            || resetCredits.Credits.Count >= resetCredits.AvailableCount)
        {
            return string.Empty;
        }

        return resetCredits.Credits.Count == 0
            ? "Expiry details unavailable"
            : $"Expiry shown for {resetCredits.Credits.Count} of {resetCredits.AvailableCount} resets";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        this.OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class UsageWindowViewModel : INotifyPropertyChanged
{
    private readonly DateTimeOffset? _resetsAt;
    private readonly bool _isUnlimited;
    private string _accessibleName = string.Empty;
    private string _resetText = string.Empty;

    public UsageWindowViewModel(
        UsageWindow window,
        DateTimeOffset now,
        TimeDisplayPrecision precision)
    {
        this.DisplayName = window.DisplayName;
        this.UsedPercent = window.UsedPercent;
        this.HasUsageMeter = window.UsageKnown && !window.IsUnlimited;
        this.PercentText = window.IsUnlimited
            ? "Unlimited"
            : window.UsageKnown
                ? UsageText.FormatPercentage(window.UsedPercent) + " used"
                : "Usage unavailable";
        this._resetsAt = window.ResetsAt;
        this._isUnlimited = window.IsUnlimited;
        this.ExactResetText = window.IsUnlimited
            ? "No quota limit"
            : UsageText.FormatExactReset(window.ResetsAt);
        this.UpdateTime(now, precision);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplayName { get; }

    public double UsedPercent { get; }

    public string PercentText { get; }

    public bool HasUsageMeter { get; }

    public string ResetText
    {
        get => this._resetText;
        private set
        {
            if (this._resetText == value)
            {
                return;
            }

            this._resetText = value;
            this.OnPropertyChanged();
        }
    }

    public string ExactResetText { get; }

    public string AccessibleName
    {
        get => this._accessibleName;
        private set
        {
            if (this._accessibleName == value)
            {
                return;
            }

            this._accessibleName = value;
            this.OnPropertyChanged();
        }
    }

    public void UpdateTime(DateTimeOffset now, TimeDisplayPrecision precision)
    {
        this.ResetText = this._isUnlimited
            ? "No quota limit"
            : UsageText.FormatResetCountdown(this._resetsAt, now, precision);
        this.AccessibleName = $"{this.DisplayName}, {this.PercentText}, {this.ResetText}";
    }

    public void RefreshVisualState() => this.OnPropertyChanged(nameof(this.UsedPercent));

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record RateLimitResetCreditViewModel
{
    public RateLimitResetCreditViewModel(RateLimitResetCredit credit, int number)
    {
        this.DisplayName = $"Reset {number}";
        this.ExpiryText = UsageText.FormatExactExpiry(credit.ExpiresAt);
        this.AccessibleName = $"{this.DisplayName}, {this.ExpiryText}";
    }

    public string DisplayName { get; }

    public string ExpiryText { get; }

    public string AccessibleName { get; }
}

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class UsageLevelBrushConverter : IValueConverter
{
    private static readonly UISettings SystemColours = new();
    private static readonly SolidColorBrush HealthyBrush = new(Windows.UI.Color.FromArgb(255, 34, 197, 94));
    private static readonly SolidColorBrush WatchBrush = new(Windows.UI.Color.FromArgb(255, 245, 158, 11));
    private static readonly SolidColorBrush LowBrush = new(Windows.UI.Color.FromArgb(255, 239, 68, 68));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (App.IsHighContrastEnabled)
        {
            return new SolidColorBrush(SystemColours.GetColorValue(UIColorType.Foreground));
        }

        double usedPercent = value is double percent ? percent : 0;
        double remainingPercent = 100 - Math.Clamp(usedPercent, 0, 100);
        return remainingPercent switch
        {
            > 50 => HealthyBrush,
            > 20 => WatchBrush,
            _ => LowBrush,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class ProviderStatusBrushConverter : IValueConverter
{
    private static readonly UISettings SystemColours = new();
    private static readonly SolidColorBrush DarkThemeSuccessBrush =
        new(Windows.UI.Color.FromArgb(255, 108, 203, 142));
    private static readonly SolidColorBrush DarkThemeWarningBrush =
        new(Windows.UI.Color.FromArgb(255, 251, 191, 36));
    private static readonly SolidColorBrush LightThemeSuccessBrush =
        new(Windows.UI.Color.FromArgb(255, 16, 124, 65));
    private static readonly SolidColorBrush LightThemeWarningBrush =
        new(Windows.UI.Color.FromArgb(255, 138, 75, 0));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        Windows.UI.Color foreground = SystemColours.GetColorValue(UIColorType.Foreground);
        if (App.IsHighContrastEnabled || value is not ProviderStatusVisualLevel level)
        {
            return new SolidColorBrush(foreground);
        }

        bool isDark = ((App)Application.Current).CurrentSettings.Theme switch
        {
            AppThemePreference.Dark => true,
            AppThemePreference.Light => false,
            _ => SystemColours.GetColorValue(UIColorType.Background) is Windows.UI.Color background
                && background.R + background.G + background.B < 384,
        };
        return level switch
        {
            ProviderStatusVisualLevel.Success => isDark ? DarkThemeSuccessBrush : LightThemeSuccessBrush,
            ProviderStatusVisualLevel.Warning => isDark ? DarkThemeWarningBrush : LightThemeWarningBrush,
            _ => new SolidColorBrush(foreground),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
