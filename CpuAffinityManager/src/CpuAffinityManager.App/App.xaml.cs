using System.Windows;
using CpuAffinityManager;  // LogConfig
using Serilog;

namespace CpuAffinityManager.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        LogConfig.Initialize("wpf");
        Log.Information("CPU Affinity Manager (WPF) starting...");

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogConfig.Shutdown();
        base.OnExit(e);
    }
}
