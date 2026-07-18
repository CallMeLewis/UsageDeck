using System.Text.Json;
using UsageDeck.Infrastructure.Providers.Claude;

namespace UsageDeck.Infrastructure.Tests;

public sealed class ClaudeUsageDiagnosticsTests
{
    [Fact]
    public void CreateReturnsStructuredDataWithoutCapturedSecrets()
    {
        const string output = """
            Account developer@example.com
            Workspace C:\Users\PrivateName\Project
            Token secret-token-value
            usage limits
            Current session
            25% used
            Resets 4pm
            Current week (PrivateModelName)
            60% used
            """;

        ClaudeUsageDiagnostic diagnostic = ClaudeUsageDiagnostics.Create(
            output,
            new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        string json = JsonSerializer.Serialize(diagnostic);

        Assert.Null(diagnostic.SafeError);
        Assert.Equal(["session", "weekly-model"], diagnostic.Windows.Select(window => window.Kind));
        Assert.DoesNotContain("developer@example.com", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateName", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateModelName", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token-value", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateReturnsSafeFailureWithoutRawCapture()
    {
        const string output = "developer@example.com secret-token-value unexpected output";

        ClaudeUsageDiagnostic diagnostic = ClaudeUsageDiagnostics.Create(output, DateTimeOffset.UtcNow);
        string json = JsonSerializer.Serialize(diagnostic);

        Assert.NotNull(diagnostic.SafeError);
        Assert.Empty(diagnostic.Windows);
        Assert.DoesNotContain(output, json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token-value", json, StringComparison.Ordinal);
    }
}
