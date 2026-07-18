namespace UsageDeck.Infrastructure.Compatibility;

/// <summary>
/// Identifiers retained so upgrades can reuse data and registrations created by earlier releases.
/// </summary>
public static class LegacyInstallIdentity
{
    public const string LocalDataDirectoryName = "CodexBarWin";

    public const string CredentialTargetPrefix = "CodexBarWin";

    public const string MainInstanceKey = "CodexBarWin.Main.v1";
}
