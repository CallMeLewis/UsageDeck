namespace CodexBarWin.Infrastructure.Security;

public interface ISecretStore
{
    bool Contains(string name);

    string? Read(string name);

    void Write(string name, string secret);

    void Delete(string name);
}

public sealed class SecretStoreException : Exception
{
    public SecretStoreException(string safeMessage, Exception innerException)
        : base(safeMessage, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeMessage);
        this.SafeMessage = safeMessage.Trim();
    }

    public string SafeMessage { get; }
}
