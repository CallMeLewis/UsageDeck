using System.IO.Compression;
using System.Xml.Linq;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.System;

namespace UsageDeck.Bootstrap;

internal static class WindowsAppRuntimeDeployment
{
    private const string MicrosoftPublisher =
        "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    private const string MicrosoftPublisherId = "8wekyb3d8bbwe";
    private const string RuntimeDirectoryName = "WindowsAppRuntime";

    private static readonly string[] PackageFileNames =
    [
        "Microsoft.WindowsAppRuntime.2.msix",
        "Microsoft.WindowsAppRuntime.Main.2.msix",
        "Microsoft.WindowsAppRuntime.Singleton.2.msix",
        "Microsoft.WindowsAppRuntime.DDLM.2.msix",
    ];

    public static void ValidatePackageFiles(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        string runtimeDirectory = Path.Combine(applicationDirectory, RuntimeDirectoryName);
        foreach (string packageFileName in PackageFileNames)
        {
            _ = ReadPackage(Path.Combine(runtimeDirectory, packageFileName));
        }
    }

    public static async Task EnsureReadyAsync(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        string runtimeDirectory = Path.Combine(applicationDirectory, RuntimeDirectoryName);
        PackageManager packageManager = new();
        foreach (string packageFileName in PackageFileNames)
        {
            string packagePath = Path.Combine(runtimeDirectory, packageFileName);
            RuntimePackage package = ReadPackage(packagePath);
            Package? registeredPackage = FindRegisteredPackage(packageManager, package);
            if (registeredPackage?.Status.VerifyIsOK() == true)
            {
                continue;
            }

            DeploymentOptions deploymentOptions = package.IsSingleton
                ? DeploymentOptions.ForceTargetApplicationShutdown
                : DeploymentOptions.None;

            if (registeredPackage is not null
                && ToVersion(registeredPackage.Id.Version) > package.Version)
            {
                await packageManager.RegisterPackageByFullNameAsync(
                    registeredPackage.Id.FullName,
                    dependencyPackageFullNames: null,
                    deploymentOptions);
            }
            else
            {
                await packageManager.AddPackageAsync(
                    new Uri(packagePath),
                    dependencyPackageUris: null,
                    deploymentOptions);
            }

            Package? installedPackage = FindRegisteredPackage(packageManager, package);
            if (installedPackage?.Status.VerifyIsOK() != true)
            {
                throw new InvalidOperationException(
                    $"Windows did not register the required package '{package.Name}'.");
            }
        }
    }

    private static Package? FindRegisteredPackage(PackageManager packageManager, RuntimePackage requiredPackage)
    {
        string familyName = $"{requiredPackage.Name}_{MicrosoftPublisherId}";
        return packageManager
            .FindPackagesForUserWithPackageTypes(
                string.Empty,
                familyName,
                PackageTypes.Framework | PackageTypes.Main)
            .Where(package => package.Id.Architecture == ProcessorArchitecture.X64)
            .Where(package => ToVersion(package.Id.Version) >= requiredPackage.Version)
            .OrderByDescending(package => ToVersion(package.Id.Version))
            .FirstOrDefault();
    }

    private static RuntimePackage ReadPackage(string packagePath)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException(
                "A required Windows App SDK package is missing.",
                packagePath);
        }

        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry manifestEntry = archive.GetEntry("AppxManifest.xml")
            ?? throw new InvalidDataException("The runtime package has no AppxManifest.xml.");
        using Stream manifestStream = manifestEntry.Open();
        XDocument manifest = XDocument.Load(manifestStream, LoadOptions.None);
        XElement identity = manifest
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Identity")
            ?? throw new InvalidDataException("The runtime package has no identity.");

        string name = RequiredAttribute(identity, "Name");
        string publisher = RequiredAttribute(identity, "Publisher");
        string architecture = RequiredAttribute(identity, "ProcessorArchitecture");
        string versionText = RequiredAttribute(identity, "Version");
        if (!string.Equals(publisher, MicrosoftPublisher, StringComparison.Ordinal)
            || !string.Equals(architecture, "x64", StringComparison.OrdinalIgnoreCase)
            || !Version.TryParse(versionText, out Version? version))
        {
            throw new InvalidDataException("The runtime package identity is not supported.");
        }

        return new RuntimePackage(
            name,
            ToVersion(version),
            name.Contains("Singleton", StringComparison.Ordinal));
    }

    private static string RequiredAttribute(XElement element, string name) =>
        element.Attribute(name)?.Value
        ?? throw new InvalidDataException($"The runtime package identity has no {name} attribute.");

    private static ulong ToVersion(PackageVersion version) =>
        ((ulong)version.Major << 48)
        | ((ulong)version.Minor << 32)
        | ((ulong)version.Build << 16)
        | version.Revision;

    private static ulong ToVersion(Version version) =>
        ((ulong)version.Major << 48)
        | ((ulong)version.Minor << 32)
        | ((ulong)version.Build << 16)
        | (uint)version.Revision;

    private sealed record RuntimePackage(string Name, ulong Version, bool IsSingleton);
}
