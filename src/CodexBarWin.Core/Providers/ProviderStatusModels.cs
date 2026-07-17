namespace CodexBarWin.Core.Providers;

public enum ProviderServiceHealth
{
    Unknown,
    Operational,
    ProblemsReported,
    OfficialStatusUnavailable,
}

public sealed record ProviderServiceStatusSnapshot(
    ProviderId ProviderId,
    ProviderServiceHealth Health,
    string Summary,
    DateTimeOffset? CheckedAt,
    Uri? OfficialStatusUri = null,
    Uri? IncidentUri = null,
    bool IsStale = false,
    string? SafeError = null)
{
    public bool HasProblems => this.Health == ProviderServiceHealth.ProblemsReported;
}
