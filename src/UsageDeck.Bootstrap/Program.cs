using System.ComponentModel;
using System.Diagnostics;
using Velopack;

namespace UsageDeck.Bootstrap;

internal static class Program
{
    private const string ApplicationFileName = "UsageDeck.App.exe";
    private const int EarlyExitDetectionMilliseconds = 10_000;

    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();

        if (args is ["--validate-runtime-packages"])
        {
            WindowsAppRuntimeDeployment.ValidatePackageFiles(AppContext.BaseDirectory);
            return 0;
        }

        while (true)
        {
            try
            {
                WindowsAppRuntimeDeployment
                    .EnsureReadyAsync(AppContext.BaseDirectory)
                    .GetAwaiter()
                    .GetResult();
                break;
            }
            catch (Exception exception) when (IsExpectedDeploymentFailure(exception))
            {
                if (!NativeDialog.ShowRuntimeFailure(exception.HResult))
                {
                    return 1;
                }
            }
        }

        string applicationPath = Path.Combine(AppContext.BaseDirectory, ApplicationFileName);
        try
        {
            ProcessStartInfo startInfo = new(applicationPath)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (string argument in args)
            {
                startInfo.ArgumentList.Add(argument);
            }

            DateTime launchStartedUtc = DateTime.UtcNow;
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not create the UsageDeck process.");
            if (process.WaitForExit(EarlyExitDetectionMilliseconds) && process.ExitCode != 0)
            {
                string diagnosticPath = GetStartupDiagnosticPath();
                bool currentDiagnosticExists = File.Exists(diagnosticPath)
                    && File.GetLastWriteTimeUtc(diagnosticPath) >= launchStartedUtc.AddSeconds(-1);
                if (!currentDiagnosticExists)
                {
                    NativeDialog.ShowLaunchFailure(process.ExitCode);
                }

                return 1;
            }

            return 0;
        }
        catch (Exception exception) when (exception is Win32Exception
            or FileNotFoundException
            or DirectoryNotFoundException
            or InvalidOperationException)
        {
            NativeDialog.ShowLaunchFailure(exception.HResult, GetStartupDiagnosticPath());
            return 1;
        }
    }

    private static string GetStartupDiagnosticPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UsageDeck",
        "diagnostics",
        "startup-failure.log");

    private static bool IsExpectedDeploymentFailure(Exception exception) => exception is
        FileNotFoundException
        or InvalidDataException
        or IOException
        or UnauthorizedAccessException
        or InvalidOperationException
        or System.Runtime.InteropServices.COMException;
}
