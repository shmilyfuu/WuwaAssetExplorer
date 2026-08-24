using WuwaAssetExplorer.Core.Services;

namespace WuwaAssetExplorer;

internal static class StartupLog
{
    private static readonly object Gate = new();

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                PortablePaths.EnsureDataDirectory();
                using var writer = new StreamWriter(PortablePaths.StartupLogFile, append: true);
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
            // Diagnostics must never become a startup dependency.
        }
    }
}
