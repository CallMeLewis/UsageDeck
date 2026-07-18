using UsageDeck.Core.Providers;

namespace UsageDeck.Infrastructure.Providers.Status;

public static class ProviderStatusSources
{
    public static IReadOnlyList<IProviderStatusProvider> Create(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        Dictionary<ProviderId, IProviderStatusProvider> official = new()
        {
            [ProviderId.Codex] = new StatusPageProvider(
                httpClient,
                ProviderId.Codex,
                new Uri("https://status.openai.com/api/v2/summary.json"),
                new Uri("https://status.openai.com/"),
                ["Codex API", "VS Code extension", "Codex in ChatGPT Desktop"],
                ["Codex"]),
            [ProviderId.Claude] = new StatusPageProvider(
                httpClient,
                ProviderId.Claude,
                new Uri("https://status.claude.com/api/v2/summary.json"),
                new Uri("https://status.claude.com/"),
                ["Claude Code", "Claude API (api.anthropic.com)"],
                ["Claude Code", "Claude API"]),
            [ProviderId.Copilot] = new StatusPageProvider(
                httpClient,
                ProviderId.Copilot,
                new Uri("https://www.githubstatus.com/api/v2/summary.json"),
                new Uri("https://www.githubstatus.com/"),
                ["Copilot", "Copilot AI Model Providers"],
                ["Copilot"]),
            [ProviderId.Amp] = new StatusPageProvider(
                httpClient,
                ProviderId.Amp,
                new Uri("https://ampcodestatus.com/api/v2/summary.json"),
                new Uri("https://ampcodestatus.com/"),
                ["Amp CLI", "ampcode.com"],
                ["Amp"]),
        };

        return ProviderId.Supported
            .Select(providerId => official.GetValueOrDefault(providerId)
                ?? new UnavailableProviderStatusProvider(providerId))
            .ToArray();
    }
}
