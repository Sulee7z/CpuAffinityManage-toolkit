using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CpuAffinityManager.Avalonia.ViewModels;
using CpuAffinityManager.Avalonia.Views;

namespace CpuAffinityManager.Avalonia;

/// <summary>
/// AOT-safe ViewLocator — uses explicit type mapping instead of
/// reflection-based Type.GetType(). Each ViewModel → View pair is
/// registered explicitly so the trimmer/AOT compiler can see all types.
/// </summary>
public class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> s_views = new()
    {
        [typeof(MainWindowViewModel)]   = () => new MainWindow(),
        [typeof(DashboardViewModel)]    = () => new DashboardView(),
        [typeof(ProcessListViewModel)]  = () => new ProcessListView(),
        [typeof(RuleListViewModel)]     = () => new RuleListView(),
        [typeof(AiViewModel)]           = () => new AiView(),
        [typeof(SystemToolsViewModel)]  = () => new SystemToolsView(),
        [typeof(StartupViewModel)]      = () => new StartupView(),
        [typeof(SettingsViewModel)]     = () => new SettingsView(),
    };

    public Control? Build(object? param)
    {
        if (param is null) return null;
        if (s_views.TryGetValue(param.GetType(), out var factory))
            return factory();
        return new TextBlock { Text = $"View not registered: {param.GetType().Name}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
