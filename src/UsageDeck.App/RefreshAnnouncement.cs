namespace UsageDeck.App;

/// <summary>
/// Words the single screen-reader announcement made when a refresh the user requested finishes.
/// </summary>
internal static class RefreshAnnouncement
{
    public static string Create(IReadOnlyCollection<ProviderTabViewModel> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        string[] failed = providers
            .Where(provider => provider.HasStatusMessage)
            .Select(provider => provider.DisplayName)
            .ToArray();

        if (providers.Count == 1)
        {
            string name = providers.First().DisplayName;
            return failed.Length == 0 ? $"{name} usage updated" : $"{name} couldn’t refresh";
        }

        return failed.Length switch
        {
            0 => "Usage updated",
            1 => $"Usage updated. {failed[0]} couldn’t refresh",
            _ => $"Usage updated. {failed.Length} providers couldn’t refresh",
        };
    }
}
