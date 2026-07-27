using Avalonia.Controls;
using Avalonia.Input;
using CpuAffinityManager.Avalonia.ViewModels;

namespace CpuAffinityManager.Avalonia.Views;

public partial class ProcessListView : UserControl
{
    public ProcessListView()
    {
        InitializeComponent();
    }

    // 右键某一行时,记录该行对应的进程,供“优先跑指定核心”动态子菜单使用。
    private void Row_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control c && c.DataContext is ProcessItem item
            && DataContext is ProcessListViewModel vm)
        {
            vm.ContextItem = item;
        }
    }
}
