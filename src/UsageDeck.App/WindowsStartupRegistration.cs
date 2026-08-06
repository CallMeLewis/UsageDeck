using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using Velopack.Locators;

namespace UsageDeck.App;

internal static class WindowsStartupRegistration
{
    internal const string TaskId = "UsageDeck.StartAtSignIn.v1";

    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                ActivationRegistrationManager.RegisterForStartupActivation(
                    TaskId,
                    ResolveExecutablePath());
            }
            else
            {
                ActivationRegistrationManager.UnregisterForStartupActivation(TaskId);
            }
        }
        catch (Exception exception) when (exception is
            ArgumentException
            or COMException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Windows could not change whether UsageDeck starts when you sign in.",
                exception);
        }
    }

    internal static string ResolveExecutablePath()
    {
        string? installRoot = VelopackLocator.IsCurrentSet
            ? VelopackLocator.Current.RootAppDir
            : null;
        return ResolveExecutablePath(
            AppContext.BaseDirectory,
            installRoot,
            Environment.ProcessPath);
    }

    internal static string ResolveExecutablePath(
        string appBaseDirectory,
        string? installRootDirectory,
        string? processPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);

        string? installedLauncher = CreateLauncherCandidate(installRootDirectory);
        if (installedLauncher is not null && File.Exists(installedLauncher))
        {
            return installedLauncher;
        }

        string bundledLauncher = Path.GetFullPath(
            Path.Combine(appBaseDirectory, "UsageDeck.Bootstrap.exe"));
        if (File.Exists(bundledLauncher))
        {
            return bundledLauncher;
        }

        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return Path.GetFullPath(processPath);
        }

        throw new InvalidOperationException("The UsageDeck executable path is unavailable.");
    }

    private static string? CreateLauncherCandidate(string? directory) =>
        string.IsNullOrWhiteSpace(directory)
            ? null
            : Path.GetFullPath(Path.Combine(directory, "UsageDeck.Bootstrap.exe"));
}
