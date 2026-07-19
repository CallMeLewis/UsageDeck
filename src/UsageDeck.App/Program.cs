using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace UsageDeck.App;

public static class Program
{
    private static App? _app;

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        StartupFailureReporter.InstallLastChanceHandler();
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            Application.Start(initializationCallbackParams =>
            {
                try
                {
                    DispatcherQueueSynchronizationContext context = new(
                        DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    _app = new App();
                }
                catch (Exception exception)
                {
                    StartupFailureReporter.ReportAndExit("Initialising the Windows interface", exception);
                }
            });
        }
        catch (Exception exception)
        {
            StartupFailureReporter.ReportAndExit("Starting the Windows interface", exception);
        }
    }
}
