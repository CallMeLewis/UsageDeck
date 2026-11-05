namespace UsageDeck.Core.Providers;

public interface IUsageProvider
{
    ProviderId Id { get; }

    string DisplayName { get; }

    Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken);
}

public interface ICliVersionProvider
{
    Task<string?> ReadCliVersionAsync(CancellationToken cancellationToken);
}

public interface IProviderStatusProvider
{
    ProviderId Id { get; }

    Uri? OfficialStatusUri { get; }

    Task<ProviderServiceStatusSnapshot> FetchStatusAsync(CancellationToken cancellationToken);
}

public enum ProviderErrorCategory
{
    NotInstalled,
    AuthenticationRequired,
    Unavailable,
    Transient,
    InvalidResponse,

    /// <summary>
    /// The provider's own tool has to run again before its usage can be read, for example
    /// because the sign-in it shares has lapsed while the tool was idle. This is expected
    /// rather than a failure, so it never counts towards availability alerts.
    /// </summary>
    Inactive,
}

public sealed class ProviderException : Exception
{
    public ProviderException(ProviderErrorCategory category, string safeMessage)
        : base(safeMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        this.Category = category;
        this.SafeMessage = safeMessage.Trim();
    }

    public ProviderException(ProviderErrorCategory category, string safeMessage, Exception innerException)
        : base(safeMessage, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        this.Category = category;
        this.SafeMessage = safeMessage.Trim();
    }

    public ProviderErrorCategory Category { get; }

    public DateTimeOffset? RetryNotBeforeUtc { get; init; }

    public string SafeMessage { get; }
}

public sealed class ProviderStatusException : Exception
{
    public ProviderStatusException(string safeMessage, Exception? innerException = null)
        : base(safeMessage, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        this.SafeMessage = safeMessage.Trim();
    }

    public string SafeMessage { get; }
}
