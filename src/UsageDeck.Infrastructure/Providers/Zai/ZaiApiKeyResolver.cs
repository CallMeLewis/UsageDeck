using UsageDeck.Infrastructure.Security;
using UsageDeck.Infrastructure.Settings;

namespace UsageDeck.Infrastructure.Providers.Zai;

public sealed record ZaiCredentialStatus(
    ApiKeyStorageMode StorageMode,
    bool IsConfigured,
    string StorageDescription);

public interface IZaiApiKeySource
{
    string? ReadApiKey();
}

public sealed class ZaiApiKeyResolver : IZaiApiKeySource, IDisposable
{
    public const string EnvironmentVariableName = "Z_AI_API_KEY";
    private const string SecretName = "zai-api-key";
    private readonly Func<string, string?> _environmentReader;
    private readonly Func<ApiKeyStorageMode> _storageMode;
    private readonly ISecretStore _windowsCredentialStore;
    private readonly object _sessionLock = new();
    private char[]? _sessionApiKey;
    private bool _isDisposed;

    public ZaiApiKeyResolver(
        ISecretStore windowsCredentialStore,
        Func<ApiKeyStorageMode> storageMode,
        Func<string, string?>? environmentReader = null)
    {
        ArgumentNullException.ThrowIfNull(windowsCredentialStore);
        ArgumentNullException.ThrowIfNull(storageMode);
        this._windowsCredentialStore = windowsCredentialStore;
        this._storageMode = storageMode;
        this._environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
    }

    public string? ReadApiKey()
    {
        this.ThrowIfDisposed();
        return this._storageMode() switch
        {
            ApiKeyStorageMode.WindowsCredentialManager => Clean(this._windowsCredentialStore.Read(SecretName)),
            ApiKeyStorageMode.EnvironmentVariable => Clean(this._environmentReader(EnvironmentVariableName)),
            ApiKeyStorageMode.SessionOnly => this.ReadSessionKey(),
            _ => null,
        };
    }

    public ZaiCredentialStatus GetStatus()
    {
        this.ThrowIfDisposed();
        ApiKeyStorageMode storageMode = this._storageMode();
        return storageMode switch
        {
            ApiKeyStorageMode.WindowsCredentialManager => new(
                storageMode,
                this._windowsCredentialStore.Contains(SecretName),
                "Windows Credential Manager on this PC"),
            ApiKeyStorageMode.EnvironmentVariable => new(
                storageMode,
                Clean(this._environmentReader(EnvironmentVariableName)) is not null,
                EnvironmentVariableName),
            ApiKeyStorageMode.SessionOnly => new(
                storageMode,
                this.HasSessionKey(),
                "Memory until UsageDeck exits"),
            _ => new(storageMode, false, "Not configured"),
        };
    }

    public void Save(string apiKey)
    {
        this.ThrowIfDisposed();
        string clean = Clean(apiKey)
            ?? throw new ArgumentException("Enter a Z.AI API key before saving.", nameof(apiKey));
        switch (this._storageMode())
        {
            case ApiKeyStorageMode.WindowsCredentialManager:
                this._windowsCredentialStore.Write(SecretName, clean);
                break;
            case ApiKeyStorageMode.SessionOnly:
                this.SaveSessionKey(clean);
                break;
            case ApiKeyStorageMode.EnvironmentVariable:
                throw new InvalidOperationException(
                    $"Set {EnvironmentVariableName} outside UsageDeck and restart the app.");
            default:
                throw new InvalidOperationException("The selected API-key storage mode is unsupported.");
        }
    }

    public void Delete()
    {
        this.ThrowIfDisposed();
        switch (this._storageMode())
        {
            case ApiKeyStorageMode.WindowsCredentialManager:
                this._windowsCredentialStore.Delete(SecretName);
                break;
            case ApiKeyStorageMode.SessionOnly:
                this.ClearSessionKey();
                break;
            case ApiKeyStorageMode.EnvironmentVariable:
                throw new InvalidOperationException(
                    $"Remove {EnvironmentVariableName} outside UsageDeck and restart the app.");
            default:
                throw new InvalidOperationException("The selected API-key storage mode is unsupported.");
        }
    }

    public void Dispose()
    {
        if (this._isDisposed)
        {
            return;
        }

        this.ClearSessionKey();
        this._isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private static string? Clean(string? value)
    {
        string? clean = value?.Trim();
        return string.IsNullOrEmpty(clean) ? null : clean;
    }

    private bool HasSessionKey()
    {
        lock (this._sessionLock)
        {
            return this._sessionApiKey is { Length: > 0 };
        }
    }

    private string? ReadSessionKey()
    {
        lock (this._sessionLock)
        {
            return this._sessionApiKey is { Length: > 0 } apiKey
                ? new string(apiKey)
                : null;
        }
    }

    private void SaveSessionKey(string apiKey)
    {
        char[] next = apiKey.ToCharArray();
        lock (this._sessionLock)
        {
            char[]? previous = this._sessionApiKey;
            this._sessionApiKey = next;
            if (previous is not null)
            {
                Array.Clear(previous);
            }
        }
    }

    private void ClearSessionKey()
    {
        lock (this._sessionLock)
        {
            if (this._sessionApiKey is not null)
            {
                Array.Clear(this._sessionApiKey);
                this._sessionApiKey = null;
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(this._isDisposed, this);
}
