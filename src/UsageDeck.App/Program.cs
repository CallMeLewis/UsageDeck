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
        WinRT.ComWrappersSupport.InitializeComWrappers();
        VelopackApp.Build().Run();

        Application.Start(initializationCallbackParams =>
        {
            DispatcherQueueSynchronizationContext context = new(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _app = new App();
        });
    }
}
