using Avalonia;
using CpuAffinityManager;  // LogConfig
using Serilog;
<<<<<<< HEAD
using System.Threading;
=======
>>>>>>> 07cba14d22092822ae57767f12fbf81c1eb1cba7

namespace CpuAffinityManager.Avalonia;

sealed class Program
{
<<<<<<< HEAD
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

=======
    [STAThread]
    public static void Main(string[] args)
    {
>>>>>>> 07cba14d22092822ae57767f12fbf81c1eb1cba7
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
<<<<<<< HEAD
            _singleInstance?.Dispose();
=======
>>>>>>> 07cba14d22092822ae57767f12fbf81c1eb1cba7
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
}
