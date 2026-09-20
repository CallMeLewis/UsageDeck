using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UsageDeck.App;

internal static class ClaudeUsageApiDialog
{
    private const string Warning =
        "UsageDeck will send the sign-in token stored by Claude Code to Anthropic's usage service to read your limits. "
        + "The token is sent only to Anthropic and is never stored by UsageDeck.\n\n"
        + "Anthropic does not document this service and says Claude Code sign-in is intended for Claude Code and its own apps. "
        + "Anthropic could block this check or take action on your account without notice. "
        + "You are responsible for deciding whether this use fits the terms of your Claude plan.\n\n"
        + "Leaving this off reads the same limits from the Claude Code usage screen, which is slower.";

    public static async Task<bool> ShowAsync(XamlRoot xamlRoot)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);

        ContentDialog confirmation = new()
        {
            XamlRoot = xamlRoot,
            Title = "Use the direct Claude usage check?",
            Content = new TextBlock { Text = Warning, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Turn on",
            CloseButtonText = "Keep off",
            DefaultButton = ContentDialogButton.Close,
        };

        return await confirmation.ShowAsync() == ContentDialogResult.Primary;
    }
}
