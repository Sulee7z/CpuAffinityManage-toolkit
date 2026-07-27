using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CpuAffinityManager.ProcOps;

namespace CpuAffinityManager.Avalonia.ViewModels;

public partial class StartupViewModel : ViewModelBase
{
    [ObservableProperty] private string _status = "";
    public ObservableCollection<StartupItem> Items { get; } = new();

    public StartupViewModel() => Refresh();

    [RelayCommand]
    public void Refresh()
    {
        Items.Clear();
        if (OperatingSystem.IsWindows())
        {
            foreach (var e in StartupService.List())
                Items.Add(new StartupItem { Name = e.Name, Command = e.Command, Hive = e.Hive, Location = e.Location });
        }
        Status = $"共 {Items.Count} 个开机启动项";
    }

    [RelayCommand]
    private void RemoveItem(StartupItem? item)
    {
        if (item == null || !OperatingSystem.IsWindows()) return;
        if (StartupService.Remove(item.Hive, item.Name))
        {
            Items.Remove(item);
            Status = $"已删除启动项:{item.Name}";
        }
        else
        {
            Status = $"删除失败:{item.Name}(所有用户项需管理员权限)";
        }
    }

    [RelayCommand]
    private void OpenStartupFolder()
    {
        try { Process.Start(new ProcessStartInfo("shell:startup") { UseShellExecute = true }); }
        catch (Exception ex) { Status = "无法打开启动文件夹:" + ex.Message; }
    }
}

public sealed class StartupItem
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Hive { get; init; } = "";
    public string Location { get; init; } = "";
}
