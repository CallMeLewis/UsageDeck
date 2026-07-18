namespace UsageDeck.Core.Providers;

public enum ProviderHostKind
{
    Native,
    Wsl,
}
public sealed record ProviderHost
{
    private ProviderHost(ProviderHostKind kind, string? wslDistribution)
    {
        this.Kind = kind;
        this.WslDistribution = wslDistribution;
    }

    public static ProviderHost Native { get; } = new(ProviderHostKind.Native, null);

    public ProviderHostKind Kind { get; }

    public string? WslDistribution { get; }

    public static ProviderHost Wsl(string distribution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distribution);

        string normalized = distribution.Trim();
        if (normalized.Length > 128 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("The WSL distribution name is not valid.", nameof(distribution));
        }

        return new ProviderHost(ProviderHostKind.Wsl, normalized);
    }

    public string DisplayName => this.Kind == ProviderHostKind.Native
        ? "Native CLI"
        : $"WSL · {this.WslDistribution}";
}
