using System.Reflection;

namespace CodexBarWin.App;

internal static class BuildInformation
{
    private const string UpdateRepositoryMetadataKey = "UpdateRepositoryUrl";

    public static string Version { get; } = NormaliseVersion(
        typeof(BuildInformation).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion);

    public static Uri? UpdateRepository { get; } = ParseUpdateRepositoryUrl(
        typeof(BuildInformation).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(
                attribute.Key,
                UpdateRepositoryMetadataKey,
                StringComparison.Ordinal))
            ?.Value);

    internal static string NormaliseVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "0.2.1";
        }

        string version = informationalVersion.Trim();
        int buildMetadataIndex = version.IndexOf('+', StringComparison.Ordinal);
        return buildMetadataIndex < 0 ? version : version[..buildMetadataIndex];
    }

    internal static Uri? ParseUpdateRepositoryUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim().TrimEnd('/'), UriKind.Absolute, out Uri? repository)
            || !string.Equals(repository.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return repository;
    }
}
