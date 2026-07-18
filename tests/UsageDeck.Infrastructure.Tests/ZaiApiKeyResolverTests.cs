using UsageDeck.Infrastructure.Providers.Zai;
using UsageDeck.Infrastructure.Security;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.Infrastructure.Tests;

public sealed class ZaiApiKeyResolverTests
{
    [Fact]
    public void CredentialManagerModeReadsAndReportsTheNamedSecret()
    {
        MemorySecretStore store = new();
        store.Write("zai-api-key", "credential-key");
        using ZaiApiKeyResolver resolver = new(
            store,
            () => ApiKeyStorageMode.WindowsCredentialManager);

        ZaiCredentialStatus status = resolver.GetStatus();

        Assert.True(status.IsConfigured);
        Assert.Equal("credential-key", resolver.ReadApiKey());
        Assert.DoesNotContain("credential-key", status.StorageDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentModeNeverWritesThroughTheApp()
    {
        MemorySecretStore store = new();
        using ZaiApiKeyResolver resolver = new(
            store,
            () => ApiKeyStorageMode.EnvironmentVariable,
            name => name == ZaiApiKeyResolver.EnvironmentVariableName ? "environment-key" : null);

        Assert.Equal("environment-key", resolver.ReadApiKey());
        Assert.True(resolver.GetStatus().IsConfigured);
        Assert.Throws<InvalidOperationException>(() => resolver.Save("replacement"));
        Assert.Empty(store.Values);
    }

    [Fact]
    public void SessionModeKeepsAndRemovesTheKeyWithoutPersistentStorage()
    {
        MemorySecretStore store = new();
        using ZaiApiKeyResolver resolver = new(store, () => ApiKeyStorageMode.SessionOnly);

        resolver.Save(" session-key ");

        Assert.Equal("session-key", resolver.ReadApiKey());
        Assert.True(resolver.GetStatus().IsConfigured);
        Assert.Empty(store.Values);

        resolver.Delete();

        Assert.Null(resolver.ReadApiKey());
        Assert.False(resolver.GetStatus().IsConfigured);
    }

    [Fact]
    public void ActiveStorageModeDoesNotFallBackToAnotherLocation()
    {
        MemorySecretStore store = new();
        store.Write("zai-api-key", "credential-key");
        ApiKeyStorageMode mode = ApiKeyStorageMode.SessionOnly;
        using ZaiApiKeyResolver resolver = new(
            store,
            () => mode,
            _ => "environment-key");

        Assert.Null(resolver.ReadApiKey());

        mode = ApiKeyStorageMode.WindowsCredentialManager;

        Assert.Equal("credential-key", resolver.ReadApiKey());
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
