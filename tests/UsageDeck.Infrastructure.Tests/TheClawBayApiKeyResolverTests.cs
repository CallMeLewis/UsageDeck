using UsageDeck.Infrastructure.Providers.TheClawBay;
using UsageDeck.Infrastructure.Security;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.Infrastructure.Tests;

public sealed class TheClawBayApiKeyResolverTests
{
    [Fact]
    public void CredentialManagerModeUsesTheProviderSpecificSecret()
    {
        MemorySecretStore store = new();
        using TheClawBayApiKeyResolver resolver = new(
            store,
            () => ApiKeyStorageMode.WindowsCredentialManager);

        resolver.Save(" claw-key ");

        Assert.Equal("claw-key", resolver.ReadApiKey());
        Assert.True(resolver.GetStatus().IsConfigured);
        Assert.True(resolver.HasWindowsCredential());
        Assert.DoesNotContain("claw-key", resolver.GetStatus().StorageDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentModeReadsOnlyTheDocumentedVariable()
    {
        using TheClawBayApiKeyResolver resolver = new(
            new MemorySecretStore(),
            () => ApiKeyStorageMode.EnvironmentVariable,
            name => name == TheClawBayApiKeyResolver.EnvironmentVariableName ? "environment-key" : null);

        Assert.Equal("environment-key", resolver.ReadApiKey());
        Assert.Throws<InvalidOperationException>(() => resolver.Save("replacement"));
    }

    [Fact]
    public void SessionModeCanReplaceAndDeleteAKeyWithoutPersistence()
    {
        MemorySecretStore store = new();
        using TheClawBayApiKeyResolver resolver = new(store, () => ApiKeyStorageMode.SessionOnly);

        resolver.Save("first");
        resolver.Save("second");
        Assert.Equal("second", resolver.ReadApiKey());
        Assert.Empty(store.Values);

        resolver.Delete();
        Assert.Null(resolver.ReadApiKey());
    }

    [Fact]
    public void SaveThatResumesAfterSessionDisposalIsRejected()
    {
        using ManualResetEventSlim modeEntered = new();
        using ManualResetEventSlim allowModeToReturn = new();
        int modeCalls = 0;
        using TheClawBayApiKeyResolver resolver = new(
            new MemorySecretStore(),
            () =>
            {
                if (Interlocked.Increment(ref modeCalls) == 2)
                {
                    modeEntered.Set();
                    allowModeToReturn.Wait();
                }

                return ApiKeyStorageMode.SessionOnly;
            });

        resolver.Save("first");
        Task saveTask = Task.Run(() => resolver.Save("second"));
        Assert.True(modeEntered.Wait(TimeSpan.FromSeconds(5)));

        resolver.Dispose();
        allowModeToReturn.Set();

        Assert.Throws<ObjectDisposedException>(() => saveTask.GetAwaiter().GetResult());
    }

    [Fact]
    public void DisposingSessionKeyRejectsSubsequentOperations()
    {
        TheClawBayApiKeyResolver resolver = new(
            new MemorySecretStore(),
            () => ApiKeyStorageMode.SessionOnly);
        resolver.Save("session-key");

        resolver.Dispose();

        Assert.Throws<ObjectDisposedException>(() => resolver.ReadApiKey());
        Assert.Throws<ObjectDisposedException>(() => resolver.GetStatus());
        Assert.Throws<ObjectDisposedException>(() => resolver.HasWindowsCredential());
        Assert.Throws<ObjectDisposedException>(() => resolver.Save("replacement"));
        Assert.Throws<ObjectDisposedException>(() => resolver.Delete());
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public bool Contains(string name) => this.Values.ContainsKey(name);

        public string? Read(string name) => this.Values.GetValueOrDefault(name);

        public void Write(string name, string secret) => this.Values[name] = secret;

        public void Delete(string name) => this.Values.Remove(name);
    }
}
