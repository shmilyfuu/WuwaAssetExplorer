using System.Diagnostics;
using System.Runtime.InteropServices;
using WuwaAssetExplorer.Core.Services;

namespace WuwaAssetExplorer.Launcher;

internal static class Program
{
    private const uint MbIconError = 0x00000010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint hWnd, string text, string caption, uint type);

    [STAThread]
    private static int Main()
    {
        var applicationRoot = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var runtimeDirectory = Path.Combine(applicationRoot, "Runtime");
        var targetExecutable = Path.Combine(runtimeDirectory, "WuwaAssetExplorer.exe");

        if (!File.Exists(targetExecutable))
        {
            MessageBox(0,
                $"未找到运行文件：\n{targetExecutable}\n\n请重新解压完整程序包。",
                "WuwaAssetExplorer",
                MbIconError);
            return 2;
        }

        try
        {
            var startInfo = new ProcessStartInfo(targetExecutable)
            {
                WorkingDirectory = runtimeDirectory,
                UseShellExecute = false
            };
            startInfo.Environment[PortablePaths.RootEnvironmentVariable] = applicationRoot;

            Process.Start(startInfo);
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox(0,
                $"启动 WuwaAssetExplorer 失败。\n\n{exception.Message}",
                "WuwaAssetExplorer",
                MbIconError);
            return 3;
        }
    }
}
