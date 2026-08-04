using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UsageDeck.App;

internal static class UpdateInstallationDialog
{
    public static async Task<bool> ShowAsync(XamlRoot xamlRoot, string? version)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);

        string content = string.IsNullOrWhiteSpace(version)
            ? "The update has been downloaded. UsageDeck needs to close and restart to install it."
            : $"Version {version} has been downloaded. UsageDeck needs to close and restart to install it.";
        ContentDialog confirmation = new()
        {
            XamlRoot = xamlRoot,
            Title = "Install UsageDeck update?",
            Content = content,
            PrimaryButtonText = "Install and restart",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await confirmation.ShowAsync() == ContentDialogResult.Primary;
    }
}
