using Microsoft.UI.Xaml;

namespace AvaAvaDance.Desktop;

public partial class App : Application
{
    private StudioWindow? window;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Console.Error.WriteLine(e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new StudioWindow();
        window.Activate();
    }
}
