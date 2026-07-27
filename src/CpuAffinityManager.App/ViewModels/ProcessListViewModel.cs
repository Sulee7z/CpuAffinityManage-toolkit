using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CpuAffinityManager.App.ViewModels;

/// <summary>
/// ViewModel for the process list — displays running processes with affinity info.
/// </summary>
public class ProcessListViewModel : INotifyPropertyChanged
{
    public ObservableCollection<ProcessItemViewModel> Processes { get; } = new();

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set { _filterText = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// ViewModel for a single process row in the list.
/// </summary>
public class ProcessItemViewModel
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string AffinityDisplay { get; set; } = string.Empty;
    public string MatchedRule { get; set; } = string.Empty;
    public ulong AffinityMask { get; set; }
}
