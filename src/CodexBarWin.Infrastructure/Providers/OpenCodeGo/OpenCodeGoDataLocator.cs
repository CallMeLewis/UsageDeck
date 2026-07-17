namespace CodexBarWin.Infrastructure.Providers.OpenCodeGo;

public sealed class OpenCodeGoDataLocator
{
    private readonly Func<string, string?> _environmentReader;
    private readonly string _localApplicationData;
    private readonly string _userProfile;

    public OpenCodeGoDataLocator(
        string? localApplicationData = null,
        string? userProfile = null,
        Func<string, string?>? environmentReader = null)
    {
        this._localApplicationData = localApplicationData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        this._userProfile = userProfile
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        this._environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
    }

    public string? FindDatabasePath() =>
        this.GetCandidateDatabasePaths().FirstOrDefault(File.Exists);

    public IReadOnlyList<string> GetCandidateDatabasePaths()
    {
        List<string> directories = [];
        string? xdgDataHome = this._environmentReader("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
        {
            directories.Add(Path.Combine(xdgDataHome.Trim(), "opencode"));
        }

        if (!string.IsNullOrWhiteSpace(this._localApplicationData))
        {
            directories.Add(Path.Combine(this._localApplicationData, "opencode"));
        }

        if (!string.IsNullOrWhiteSpace(this._userProfile))
        {
            directories.Add(Path.Combine(this._userProfile, ".local", "share", "opencode"));
        }

        return directories
            .Select(directory => Path.GetFullPath(Path.Combine(directory, "opencode.db")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
