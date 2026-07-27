using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using CpuAffinityManager.Avalonia.ViewModels;
using CpuAffinityManager.Avalonia.Views;

namespace CpuAffinityManager.Avalonia;

public partial class App : Application
{
    private TrayIcon? _trayIcon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate data annotation validations
            DisableAvaloniaDataAnnotationValidation();

            var mainVm = new MainWindowViewModel();
            var mainWindow = new MainWindow { DataContext = mainVm };
            desktop.MainWindow = mainWindow;

            SetupTrayIcon(desktop, mainWindow);

            // Safety net: also clean up enforcement if the app is shut down another way.
            desktop.ShutdownRequested += (_, _) => { try { mainVm.Shutdown(); } catch { } };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Adds a system-tray icon so the app can live in the tray when the window is
    /// closed with "关闭时最小化到系统托盘" enabled. The tray menu can restore the
    /// window or truly exit the app.
    /// </summary>
    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        try
        {
            var showItem = new NativeMenuItem("显示主窗口");
            showItem.Click += (_, _) => window.ShowFromTray();

            var exitItem = new NativeMenuItem("退出");
            exitItem.Click += (_, _) =>
            {
                window.ForceExit = true;
                (window.DataContext as MainWindowViewModel)?.Shutdown();
                desktop.Shutdown();
            };

            _trayIcon = new TrayIcon
            {
                ToolTipText = "CPU 亲和性管理器",
                IsVisible = true,
                Menu = new NativeMenu { showItem, exitItem }
            };

            try
            {
                using var s = AssetLoader.Open(new System.Uri("avares://CpuAffinityManager.Avalonia/Assets/app.ico"));
                _trayIcon.Icon = new WindowIcon(s);
            }
            catch { }

            // Left-click / double-click the tray icon restores the window.
            _trayIcon.Clicked += (_, _) => window.ShowFromTray();

            TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
        }
        catch
        {
            // Tray is optional — never let its failure crash startup.
        }
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        var pluginsToRemove = BindingPlugins.DataValidators
            .OfType<DataAnnotationsValidationPlugin>()
            .ToArray();
        foreach (var plugin in pluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
