using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.App.Tests;

public sealed class SettingsMutationsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "UsageDeck.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OverlappingProviderChangesPreserveBothMutations()
    {
        string path = Path.Combine(this._directory, "settings.json");
        AppSettings initial = new([ProviderId.Codex], ProviderId.Codex);
        AppSettingsStore store = new(path);
        using AppSettingsManager manager = new(store, initial);
        using ManualResetEventSlim firstMutationStarted = new();
        using ManualResetEventSlim releaseFirstMutation = new();

        Task firstUpdate = Task.Run(() => manager.UpdateAsync(settings =>
        {
            firstMutationStarted.Set();
            releaseFirstMutation.Wait();
            return SettingsMutations.SetProviderEnabled(settings, ProviderId.Claude, isEnabled: true);
        }));
        try
        {
            Assert.True(firstMutationStarted.Wait(TimeSpan.FromSeconds(5)));

            Task secondUpdate = manager.UpdateAsync(settings =>
                SettingsMutations.SetProviderEnabled(settings, ProviderId.Amp, isEnabled: true));
            Assert.False(secondUpdate.IsCompleted);

            releaseFirstMutation.Set();
            await Task.WhenAll(firstUpdate, secondUpdate);
        }
        finally
        {
            releaseFirstMutation.Set();
        }

        Assert.Equal([ProviderId.Codex, ProviderId.Claude, ProviderId.Amp], manager.Current.EnabledProviders);
        AppSettings persisted = store.Load().Settings;
        Assert.Equal(manager.Current.EnabledProviders, persisted.EnabledProviders);
        Assert.Equal(manager.Current.DefaultProvider, persisted.DefaultProvider);
    }

    [Fact]
    public async Task OverlappingEnableAndDisablePreserveBothMutations()
    {
        string path = Path.Combine(this._directory, "settings.json");
        AppSettings initial = new([ProviderId.Codex], ProviderId.Codex);
        AppSettingsStore store = new(path);
        using AppSettingsManager manager = new(store, initial);
        using ManualResetEventSlim firstMutationStarted = new();
        using ManualResetEventSlim releaseFirstMutation = new();

        Task firstUpdate = Task.Run(() => manager.UpdateAsync(settings =>
        {
            firstMutationStarted.Set();
            releaseFirstMutation.Wait();
            return SettingsMutations.SetProviderEnabled(settings, ProviderId.Claude, isEnabled: true);
        }));
        try
        {
            Assert.True(firstMutationStarted.Wait(TimeSpan.FromSeconds(5)));

            Task secondUpdate = manager.UpdateAsync(settings =>
                SettingsMutations.SetProviderEnabled(settings, ProviderId.Codex, isEnabled: false));
            Assert.False(secondUpdate.IsCompleted);

            releaseFirstMutation.Set();
            await Task.WhenAll(firstUpdate, secondUpdate);
        }
        finally
        {
            releaseFirstMutation.Set();
        }

        Assert.Equal([ProviderId.Claude], manager.Current.EnabledProviders);
        Assert.Equal(ProviderId.Claude, manager.Current.DefaultProvider);
        AppSettings persisted = store.Load().Settings;
        Assert.Equal(manager.Current.EnabledProviders, persisted.EnabledProviders);
        Assert.Equal(manager.Current.DefaultProvider, persisted.DefaultProvider);
    }

    [Fact]
    public void DisablingTheDefaultProviderSelectsTheFirstRemainingProvider()
    {
        AppSettings settings = new(
            [ProviderId.Claude, ProviderId.Amp],
            ProviderId.Claude);

        AppSettings result = SettingsMutations.SetProviderEnabled(
            settings,
            ProviderId.Claude,
            isEnabled: false);

        Assert.Equal([ProviderId.Amp], result.EnabledProviders);
        Assert.Equal(ProviderId.Amp, result.DefaultProvider);
    }

    [Fact]
    public void DisablingTheLastProviderLeavesSettingsUnchanged()
    {
        AppSettings settings = new([ProviderId.Codex], ProviderId.Codex);

        AppSettings result = SettingsMutations.SetProviderEnabled(
            settings,
            ProviderId.Codex,
            isEnabled: false);

        Assert.Same(settings, result);
    }

    public void Dispose()
    {
        if (Directory.Exists(this._directory))
        {
            Directory.Delete(this._directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
