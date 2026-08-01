using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CpuAffinityManager;
using CpuAffinityManager.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace CpuAffinityManager.App;

public partial class App : Application
{
    private IHost? _host;
    private static Mutex? _singleInstance;

    private static bool IsElevated
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            _singleInstance = new Mutex(true, @"Global\CpuAffinityManager_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                var existing = Process.GetProcessesByName("CpuAffinityManager.App")
                    .FirstOrDefault(p => p.Id != Environment.ProcessId);
                if (existing != null && !existing.HasExited)
                {
                    SetForegroundWindow(existing.MainWindowHandle);
                    ShowWindowAsync(existing.MainWindowHandle, 9);
                }
                Shutdown(0);
                return;
            }

            InitializeHost();
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            LogExceptionToFile("App.Startup", ex);
            MessageBox.Show(
                $"启动失败: {ex.Message}",
                "CPU Affinity Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host != null)
            {
                _host.StopAsync().GetAwaiter().GetResult();
                _host.Dispose();
            }
            LogConfig.Shutdown();
        }
        catch (Exception ex)
        {
            LogExceptionToFile("App.OnExit", ex);
        }
        finally
        {
            _singleInstance?.Dispose();
        }
        base.OnExit(e);
    }

    private void InitializeHost()
    {
        var configDir = Engine.RuleConfigPath.DataDirectory;
        ConfigManager.ValidateConfig(configDir);

        LogConfig.Initialize("wpf");
        Log.Information("CPU Affinity Manager (WPF) starting...");

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureAppConfiguration(c =>
            {
                c.AddJsonFile(Path.Combine(configDir, "appsettings.json"), false, true);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton(new ConfigManager(configDir));
                services.AddHostedService<ConfigInitService>();
            })
            .Build();

        _host.Start();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString());
        LogExceptionToFile("AppDomain.UnhandledException", ex);
        Log.Fatal(ex, "Unhandled AppDomain exception. IsTerminating={IsTerminating}", e.IsTerminating);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogExceptionToFile("UnobservedTaskException", e.Exception);
        Log.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private void OnDispatcherUnhandledException(
        object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogExceptionToFile("DispatcherUnhandledException", e.Exception);
        Log.Error(e.Exception, "Unhandled UI dispatcher exception");
        e.Handled = true;
    }

    private static void LogExceptionToFile(string category, Exception ex)
    {
        try
        {
            var crashDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CpuAffinityManager", "Crashes");
            Directory.CreateDirectory(crashDir);
            var crashFile = Path.Combine(crashDir,
                $"crash_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.log");
            File.WriteAllText(crashFile,
                $"""
                [{category}] {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}
                Type: {ex.GetType().FullName}
                Message: {ex.Message}
                StackTrace:
                {ex.StackTrace}

                {(ex.InnerException is not null ? $"Inner: {ex.InnerException}" : "")}
                """);
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
}
