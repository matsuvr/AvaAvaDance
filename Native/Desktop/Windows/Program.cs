using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AvaAvaDance.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("-batchmode", StringComparer.OrdinalIgnoreCase))
            return EngineBridge.RunUnity();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
        return Environment.ExitCode;
    }
}
