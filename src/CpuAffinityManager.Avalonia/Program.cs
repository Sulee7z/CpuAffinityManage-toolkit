using Avalonia;
using CpuAffinityManager;  // LogConfig
using Serilog;

namespace CpuAffinityManager.Avalonia;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        LogConfig.Initialize("av");

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal crash");
        }
        finally
        {
            LogConfig.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
}
