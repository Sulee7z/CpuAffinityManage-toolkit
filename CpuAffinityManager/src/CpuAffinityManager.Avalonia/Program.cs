using Avalonia;
using CpuAffinityManager;  // LogConfig
using Serilog;
using System.Threading;

namespace CpuAffinityManager.Avalonia;

sealed class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        _singleInstance = new Mutex(true, @"Global\CpuAffinityManager_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            _singleInstance.Dispose();
            return;
        }

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
            _singleInstance?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
}