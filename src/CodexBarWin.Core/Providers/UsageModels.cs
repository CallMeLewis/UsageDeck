namespace CodexBarWin.Core.Providers;

public enum UsageDataState
{
    Fresh,
    Stale,
    Unavailable,
    AuthenticationRequired,
}

public enum UsageConfidence
{
    Authoritative,
    Parsed,
    Estimated,
}

public sealed record AccountIdentity(string? Email, string? Plan, string? Organisation = null);

public sealed record CreditBalance(string? Balance, bool HasCredits, bool IsUnlimited);

public sealed record RateLimitResetCredit(DateTimeOffset? ExpiresAt);

public sealed record RateLimitResetCredits
{
    public RateLimitResetCredits(
        long availableCount,
        IEnumerable<RateLimitResetCredit>? credits = null)
    {
        if (availableCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableCount), "Available reset credits cannot be negative.");
        }

        this.AvailableCount = availableCount;
        this.Credits = credits?.ToArray() ?? [];
    }

    public long AvailableCount { get; }

    public IReadOnlyList<RateLimitResetCredit> Credits { get; }
}

public sealed record UsageWindow
{
    public UsageWindow(
        string id,
        string displayName,
        double usedPercent,
        DateTimeOffset? resetsAt = null,
        TimeSpan? duration = null,
        UsageConfidence confidence = UsageConfidence.Authoritative,
        bool usageKnown = true,
        bool isUnlimited = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        if (!double.IsFinite(usedPercent))
        {
            throw new ArgumentOutOfRangeException(nameof(usedPercent), "Usage must be a finite percentage.");
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "A usage-window duration must be positive.");
        }

        this.Id = id.Trim();
        this.DisplayName = displayName.Trim();
        this.UsedPercent = Math.Clamp(usedPercent, 0, 100);
        this.ResetsAt = resetsAt;
        this.Duration = duration;
        this.Confidence = confidence;
        this.UsageKnown = usageKnown;
        this.IsUnlimited = isUnlimited;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public double UsedPercent { get; }

    public double RemainingPercent => 100 - this.UsedPercent;

    public DateTimeOffset? ResetsAt { get; }

    public TimeSpan? Duration { get; }

    public UsageConfidence Confidence { get; }

    public bool UsageKnown { get; }

    public bool IsUnlimited { get; }
}

public sealed record ProviderSnapshot
{
    public ProviderSnapshot(
        ProviderId providerId,
        string displayName,
        string sourceDescription,
        DateTimeOffset capturedAt,
        UsageDataState state,
        IEnumerable<UsageWindow>? usageWindows = null,
        AccountIdentity? identity = null,
        CreditBalance? credits = null,
        RateLimitResetCredits? resetCredits = null,
        string? safeError = null,
        string? cliVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDescription);

        this.ProviderId = providerId;
        this.DisplayName = displayName.Trim();
        this.SourceDescription = sourceDescription.Trim();
        this.CapturedAt = capturedAt;
        this.State = state;
        this.UsageWindows = usageWindows?.ToArray() ?? [];
        this.Identity = identity;
        this.Credits = credits;
        this.ResetCredits = resetCredits;
        this.SafeError = string.IsNullOrWhiteSpace(safeError) ? null : safeError.Trim();
        this.CliVersion = string.IsNullOrWhiteSpace(cliVersion) ? null : cliVersion.Trim();
    }

    public ProviderId ProviderId { get; }

    public string DisplayName { get; }

    public string SourceDescription { get; }

    public DateTimeOffset CapturedAt { get; }

    public UsageDataState State { get; }

    public IReadOnlyList<UsageWindow> UsageWindows { get; }

    public AccountIdentity? Identity { get; }

    public CreditBalance? Credits { get; }

    public RateLimitResetCredits? ResetCredits { get; }

    public string? SafeError { get; }

    public string? CliVersion { get; }

    public double HighestUsedPercent => this.UsageWindows.Count == 0
        ? 0
        : this.UsageWindows.Max(window => window.UsedPercent);

    public ProviderSnapshot WithFailure(UsageDataState state, string safeError) => new(
        this.ProviderId,
        this.DisplayName,
        this.SourceDescription,
        this.CapturedAt,
        state,
        this.UsageWindows,
        this.Identity,
        this.Credits,
        this.ResetCredits,
        safeError,
        this.CliVersion);

    public ProviderSnapshot WithCliVersion(string? cliVersion) => new(
        this.ProviderId,
        this.DisplayName,
        this.SourceDescription,
        this.CapturedAt,
        this.State,
        this.UsageWindows,
        this.Identity,
        this.Credits,
        this.ResetCredits,
        this.SafeError,
        cliVersion);
}
