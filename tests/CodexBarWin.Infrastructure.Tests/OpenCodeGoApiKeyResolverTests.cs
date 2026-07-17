using CodexBarWin.Infrastructure.Providers.OpenCodeGo;
using CodexBarWin.Infrastructure.Security;
using CodexBarWin.Infrastructure.Settings;

namespace CodexBarWin.Infrastructure.Tests;

public sealed class OpenCodeGoApiKeyResolverTests
{
    [Fact]
    public void ReadsCredentialManagerKeyWithoutPersistingItInSettings()
    {
        MemorySecretStore store = new();
        using OpenCodeGoApiKeyResolver resolver = new(
            store,
            () => ApiKeyStorageMode.WindowsCredentialManager);

        resolver.Save("oc_sk_credential");

        Assert.Equal("oc_sk_credential", resolver.ReadApiKey());
        Assert.True(resolver.GetStatus().IsConfigured);
    }

    [Fact]
    public void ReadsEnvironmentVariableWithoutWritingIt()
    {
        MemorySecretStore store = new();
        using OpenCodeGoApiKeyResolver resolver = new(
            store,
            () => ApiKeyStorageMode.EnvironmentVariable,
            name => name == OpenCodeGoApiKeyResolver.EnvironmentVariableName ? "oc_sk_environment" : null);

        Assert.Equal("oc_sk_environment", resolver.ReadApiKey());
        Assert.Throws<InvalidOperationException>(() => resolver.Save("oc_sk_replacement"));
        Assert.Empty(store.Secrets);
    }

    [Fact]
    public void SessionKeyCanBeSavedAndRemoved()
    {
        using OpenCodeGoApiKeyResolver resolver = new(
            new MemorySecretStore(),
            () => ApiKeyStorageMode.SessionOnly);

        resolver.Save("oc_sk_session");
        Assert.Equal("oc_sk_session", resolver.ReadApiKey());

        resolver.Delete();
        Assert.Null(resolver.ReadApiKey());
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        public Dictionary<string, string> Secrets { get; } = [];

        public bool Contains(string name) => this.Secrets.ContainsKey(name);

        public string? Read(string name) => this.Secrets.GetValueOrDefault(name);

        public void Write(string name, string secret) => this.Secrets[name] = secret;

        public void Delete(string name) => this.Secrets.Remove(name);
    }
}
