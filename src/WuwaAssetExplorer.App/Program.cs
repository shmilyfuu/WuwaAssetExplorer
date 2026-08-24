using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WuwaAssetExplorer;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        StartupLog.Write("Process entry");

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            StartupLog.Write("ComWrappers initialized");

            Application.Start(_ =>
            {
                StartupLog.Write("WinUI Application.Start callback entered");
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
                StartupLog.Write("App constructed");
            });

            StartupLog.Write("Application.Start returned");
        }
        catch (Exception ex)
        {
            StartupLog.Write("Fatal startup exception", ex);
            throw;
        }
    }
}

internal static class StartupLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "Data", "startup.log");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                using var writer = new StreamWriter(LogPath, append: true);
                writer.Write(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
                writer.Write(" | ");
                writer.Write(message);
                if (exception is not null)
                {
                    writer.WriteLine();
                    writer.Write(exception);
                }
                writer.WriteLine();
            }
        }
        catch
        {
            // Startup logging must never become a startup dependency.
        }
    }
}
