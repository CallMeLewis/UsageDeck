using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App.Tests;

public sealed class TrayToolTipTextTests
{
    private const string KimiCodeMessage = "Kimi Code usage updates while you're using Kimi Code.";

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ShowsTheMostUsedWindowWithItsResetCountdown()
    {
        ProviderSnapshot codex = CreateSnapshot(
            ProviderId.Codex,
            new UsageWindow("five-hour", "5-hour", 42, Now.AddHours(1).AddMinutes(12)),
            new UsageWindow("weekly", "Weekly", 92, Now.AddDays(3)));

        string text = Create([ProviderId.Codex], codex);

        Assert.Equal("UsageDeck\nOpenAI Codex: 92% used, resets in 3d", text);
    }

    [Fact]
    public void FollowsTheRemainingUsagePreference()
    {
        ProviderSnapshot claude = CreateSnapshot(
            ProviderId.Claude,
            new UsageWindow("session", "Session", 85, Now.AddMinutes(30)));

        string text = TrayToolTipText.Create(
            [ProviderId.Claude],
            _ => claude,
            Now,
            UsageValueDisplayMode.Remaining);

        Assert.Equal("UsageDeck\nClaude: 15% remaining, resets in 30m", text);
    }

    [Fact]
    public void ExplainsProblemsInsteadOfShowingEmptyValues()
    {
        ProviderSnapshot claude = new(
            ProviderId.Claude,
            "Claude",
            "Claude Code CLI",
            Now,
            UsageDataState.AuthenticationRequired,
            safeError: "Sign in to Claude Code.",
            errorCategory: ProviderErrorCategory.AuthenticationRequired);
        ProviderSnapshot amp = new(
            ProviderId.Amp,
            "Amp",
            "Amp CLI",
            Now,
            UsageDataState.Unavailable,
            safeError: "Amp opened, but its usage details could not be read.",
            errorCategory: ProviderErrorCategory.InvalidResponse);
        Dictionary<ProviderId, ProviderSnapshot> snapshots = new()
        {
            [ProviderId.Claude] = claude,
            [ProviderId.Amp] = amp,
        };

        string text = TrayToolTipText.Create(
            [ProviderId.Codex, ProviderId.Claude, ProviderId.Amp],
            snapshots.GetValueOrDefault,
            Now,
            UsageValueDisplayMode.Used);

        Assert.Equal(
            "UsageDeck\nOpenAI Codex: no data yet\nClaude: sign-in needed\nAmp: couldn’t refresh",
            text);
    }

    [Fact]
    public void LabelsSavedReadings()
    {
        ProviderSnapshot stale = CreateSnapshot(
            ProviderId.Codex,
            new UsageWindow("five-hour", "5-hour", 30, Now.AddHours(2)))
            .WithFailure(UsageDataState.Stale, "Codex could not be reached.", ProviderErrorCategory.Transient);

        string text = Create([ProviderId.Codex], stale);

        Assert.Equal("UsageDeck\nOpenAI Codex: 30% used, resets in 2h (saved data)", text);
    }

    [Fact]
    public void InactiveProvidersKeepSavedReadingsWithoutBeingTreatedAsProblems()
    {
        ProviderSnapshot kimi = CreateSnapshot(
            ProviderId.KimiCode,
            new UsageWindow("5-hour", "5-hour limit", 40, Now.AddHours(2)))
            .WithFailure(UsageDataState.Stale, KimiCodeMessage, ProviderErrorCategory.Inactive);
        ProviderSnapshot kimiWithoutReading = new(
            ProviderId.KimiCode,
            "Kimi Code",
            "No source",
            DateTimeOffset.MinValue,
            UsageDataState.Unavailable,
            safeError: KimiCodeMessage,
            errorCategory: ProviderErrorCategory.Inactive);

        Assert.Equal("UsageDeck\nKimi Code: 40% used, resets in 2h (saved data)", Create([ProviderId.KimiCode], kimi));
        Assert.Equal("UsageDeck\nKimi Code: no data yet", Create([ProviderId.KimiCode], kimiWithoutReading));

        // When lines must be dropped, a failed refresh outranks a provider that is simply inactive.
        ProviderId[] providers =
        [
            ProviderId.KimiCode,
            ProviderId.Codex,
            ProviderId.Claude,
            ProviderId.Antigravity,
            ProviderId.Copilot,
            ProviderId.Amp,
        ];
        Dictionary<ProviderId, ProviderSnapshot> snapshots = providers.ToDictionary(
            provider => provider,
            provider => CreateSnapshot(provider, new UsageWindow("weekly", "Weekly", 10, Now.AddDays(1))));
        snapshots[ProviderId.KimiCode] = snapshots[ProviderId.KimiCode]
            .WithFailure(UsageDataState.Stale, KimiCodeMessage, ProviderErrorCategory.Inactive);
        snapshots[ProviderId.Amp] = snapshots[ProviderId.Amp]
            .WithFailure(UsageDataState.Stale, "Amp could not be reached.", ProviderErrorCategory.Transient);

        string[] lines = TrayToolTipText.Create(providers, snapshots.GetValueOrDefault, Now, UsageValueDisplayMode.Used)
            .Split('\n');

        Assert.StartsWith("Amp: 10% used", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("Kimi Code: 10% used", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void OmitsHiddenCodexSparkWindows()
    {
        ProviderSnapshot codex = CreateSnapshot(
            ProviderId.Codex,
            new UsageWindow("five-hour", "5-hour", 20, Now.AddHours(2)),
            new UsageWindow("codex-spark", "GPT-5.3 Spark", 90, Now.AddHours(1)));

        string text = TrayToolTipText.Create(
            [ProviderId.Codex],
            _ => codex,
            Now,
            UsageValueDisplayMode.Used,
            showCodexSparkCard: false);

        Assert.Equal("UsageDeck\nOpenAI Codex: 20% used, resets in 2h", text);
    }

    [Fact]
    public void KeepsTheMostUrgentProvidersWhenEveryLineCannotFit()
    {
        ProviderId[] providers =
        [
            ProviderId.Codex,
            ProviderId.Claude,
            ProviderId.Antigravity,
            ProviderId.Copilot,
            ProviderId.Kiro,
            ProviderId.Amp,
        ];
        Dictionary<ProviderId, ProviderSnapshot> snapshots = providers.ToDictionary(
            provider => provider,
            provider => CreateSnapshot(
                provider,
                new UsageWindow(
                    "weekly",
                    "Weekly",
                    provider == ProviderId.Kiro ? 96 : 10,
                    Now.AddDays(1).AddHours(23).AddMinutes(59))));

        string text = TrayToolTipText.Create(
            providers,
            snapshots.GetValueOrDefault,
            Now,
            UsageValueDisplayMode.Used);

        Assert.True(text.Length <= TrayToolTipText.MaximumLength);
        string[] lines = text.Split('\n');
        Assert.Equal("UsageDeck", lines[0]);
        Assert.StartsWith("Kiro: 96% used", lines[1], StringComparison.Ordinal);
        Assert.Matches(@"^\+\d more$", lines[^1]);
    }

    private static string Create(ProviderId[] providers, ProviderSnapshot snapshot) =>
        TrayToolTipText.Create(providers, _ => snapshot, Now, UsageValueDisplayMode.Used);

    private static ProviderSnapshot CreateSnapshot(ProviderId provider, params UsageWindow[] windows) =>
        new(provider, provider.DisplayName, "Test source", Now, UsageDataState.Fresh, windows);
}
