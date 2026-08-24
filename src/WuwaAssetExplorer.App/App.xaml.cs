using Microsoft.UI.Xaml;

namespace WuwaAssetExplorer;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        StartupLog.Write("App constructor entered");
        InitializeComponent();
        UnhandledException += App_UnhandledException;
        StartupLog.Write("App XAML initialized");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupLog.Write("OnLaunched entered");
        _window = new MainWindow();
        StartupLog.Write("MainWindow constructed");
        _window.Activate();
        StartupLog.Write("MainWindow activated");
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        StartupLog.Write("WinUI unhandled exception", e.Exception);
    }
}
