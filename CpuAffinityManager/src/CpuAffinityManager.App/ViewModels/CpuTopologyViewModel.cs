using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CpuAffinityManager.Cpu;

namespace CpuAffinityManager.App.ViewModels;

/// <summary>
/// ViewModel for the CPU topology visualization.
/// </summary>
public class CpuTopologyViewModel : INotifyPropertyChanged
{
    private CpuTopology? _topology;

    public CpuTopology? Topology
    {
        get => _topology;
        set { _topology = value; OnPropertyChanged(); UpdateCoreList(); }
    }

    public ObservableCollection<CoreItemViewModel> Cores { get; } = new();

    public int TotalLogicalProcessors => _topology?.TotalLogicalProcessors ?? 0;
    public int PcoreCount => _topology?.PcoreCount ?? 0;
    public int EcoreCount => _topology?.EcoreCount ?? 0;
    public bool SmtEnabled => _topology?.SmtEnabled ?? false;

    public string PcoreMaskHex => _topology != null ? $"0x{_topology.PcoreMask:X}" : "N/A";
    public string EcoreMaskHex => _topology != null ? $"0x{_topology.EcoreMask:X}" : "N/A";

    private void UpdateCoreList()
    {
        Cores.Clear();
        if (_topology == null) return;

        for (int i = 0; i < _topology.TotalLogicalProcessors && i < 64; i++)
        {
            ulong bit = 1UL << i;
            string coreType = "Unknown";
            if ((_topology.PcoreMask & bit) != 0)
                coreType = (_topology.Smt1Mask & bit) != 0 ? "P-core (SMT)" : "P-core";
            else if ((_topology.EcoreMask & bit) != 0)
                coreType = "E-core";

            Cores.Add(new CoreItemViewModel
            {
                LogicalProcessorIndex = i,
                CoreType = coreType
            });
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// ViewModel for a single core entry in the topology list.
/// </summary>
public class CoreItemViewModel
{
    public int LogicalProcessorIndex { get; set; }
    public string CoreType { get; set; } = "Unknown";
}
