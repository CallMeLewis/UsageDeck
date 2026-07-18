namespace UsageDeck.Core.Providers;

public readonly record struct ProviderId
{
    public static readonly ProviderId All = new("all");
    public static readonly ProviderId Codex = new("codex");
    public static readonly ProviderId Claude = new("claude");
    public static readonly ProviderId Antigravity = new("antigravity");
    public static readonly ProviderId Copilot = new("copilot");
    public static readonly ProviderId Kiro = new("kiro");
    public static readonly ProviderId Amp = new("amp");
    public static readonly ProviderId OpenCodeGo = new("opencode-go");
    public static readonly ProviderId Zai = new("zai");

    public static IReadOnlyList<ProviderId> Supported { get; } =
        [Codex, Claude, Antigravity, Copilot, Kiro, Amp, OpenCodeGo, Zai];

    public ProviderId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string normalized = value.Trim().ToLowerInvariant();
        if (normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Provider identifiers may contain only letters, numbers, hyphens, and underscores.", nameof(value));
        }

        this.Value = normalized;
    }

    public string Value { get; }

    public string DisplayName => this.Value switch
    {
        "all" => "All providers",
        "codex" => "OpenAI Codex",
        "claude" => "Claude",
        "antigravity" => "Antigravity",
        "copilot" => "GitHub Copilot",
        "kiro" => "Kiro",
        "amp" => "Amp",
        "opencode-go" => "OpenCode Go",
        "zai" => "Z.AI",
        _ => this.Value,
    };

    public override string ToString() => this.Value;
}
