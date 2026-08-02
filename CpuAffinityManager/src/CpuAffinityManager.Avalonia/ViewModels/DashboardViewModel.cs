using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;
using CpuAffinityManager.ProcOps;
using Avalonia.Media;

namespace CpuAffinityManager.Avalonia.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly IRuleEngine _ruleEngine;
    private readonly ICpuTopologyService _topoService;
    private DispatcherTimer? _statsTimer;

    // Shared immutable brushes — parsed and frozen once instead of on every Refresh.
    // Win11-aligned core-type colors (readable with white text on both light/dark tiles).
    private static readonly IBrush PcoreBrush = new SolidColorBrush(Color.Parse("#C42B1C")).ToImmutable();
    private static readonly IBrush PcoreSmtBrush = new SolidColorBrush(Color.Parse("#CA5010")).ToImmutable();
    private static readonly IBrush EcoreBrush = new SolidColorBrush(Color.Parse("#005FB8")).ToImmutable();
    private static readonly IBrush LogicalBrush = new SolidColorBrush(Color.Parse("#8A8A8A")).ToImmutable();
    private static readonly IBrush JobLockedBrush = new SolidColorBrush(Color.Parse("#A4262C")).ToImmutable();

    [ObservableProperty] private string _processCount = "--";
    [ObservableProperty] private string _rulesActive = "--";
    [ObservableProperty] private string _pCoreCount = "--";
    [ObservableProperty] private string _eCoreCount = "--";
    [ObservableProperty] private string _cpuUsage = "--";
    [ObservableProperty] private string _memUsage = "--";
    [ObservableProperty] private bool _hasTopology;

    public ObservableCollection<CoreVisualItem> CoreItems { get; } = new();
    public ObservableCollection<RuleSummaryItem> RuleSummaries { get; } = new();

    public DashboardViewModel(IRuleEngine ruleEngine, ICpuTopologyService topoService)
    {
        _ruleEngine = ruleEngine;
        _topoService = topoService;
    }

    /// <summary>
    /// Starts the 2-second live CPU/RAM usage sampler (UI thread timer). Called once
    /// from app initialization; <see cref="StopStatsTimer"/> stops it on shutdown.
    /// </summary>
    public void StartStatsTimer()
    {
        if (_statsTimer == null)
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statsTimer.Tick -= StatsTimerTick;
        _statsTimer.Tick += StatsTimerTick;
        if (!_statsTimer.IsEnabled)
            _statsTimer.Start();
        RefreshStats();
    }

    public void StopStatsTimer()
    {
        _statsTimer?.Stop();
    }

    private void StatsTimerTick(object? sender, EventArgs e) => RefreshStats();

    private void RefreshStats()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                CpuUsage = "N/A";
                MemUsage = "N/A";
                return;
            }

            var (cpu, totalMb, usedMb) = SystemStatsService.Sample();
            CpuUsage = $"{cpu:0.0}%";
            MemUsage = totalMb > 0
                ? $"{usedMb / 1024.0:0.0} / {totalMb / 1024.0:0.0} GB"
                : "--";
        }
        catch
        {
            CpuUsage = "--";
            MemUsage = "--";
        }
    }

    public void Refresh()
    {
        var topo = _topoService.Detect();
        HasTopology = topo != null;

        try { ProcessCount = System.Diagnostics.Process.GetProcesses().Length.ToString(); }
        catch { ProcessCount = "??"; }
        RulesActive = _ruleEngine.Rules.Count(r => r.Enabled).ToString();
        PCoreCount = topo.PcoreCount.ToString();
        ECoreCount = topo.EcoreCount.ToString();

        // Core visualization
        CoreItems.Clear();
        for (int i = 0; i < topo.TotalLogicalProcessors && i < 64; i++)
        {
            ulong bit = 1UL << i;
            string type;
            IBrush color;

            if ((topo.PcoreMask & bit) != 0)
            {
                bool smt = (topo.Smt1Mask & bit) != 0;
                type = smt ? "P-core (SMT)" : "P-core";
                color = smt ? PcoreSmtBrush : PcoreBrush;
            }
            else if ((topo.EcoreMask & bit) != 0)
            {
                type = "E-core";
                color = EcoreBrush;
            }
            else
            {
                type = "Logical";
                color = LogicalBrush;
            }

            CoreItems.Add(new CoreVisualItem
            {
                Index = i,
                ColorBrush = color,
                Tooltip = $"LP#{i}: {type}"
            });
        }

        // Rule summaries
        RuleSummaries.Clear();
        foreach (var r in _ruleEngine.Rules.Where(r => r.Enabled))
        {
            RuleSummaries.Add(new RuleSummaryItem
            {
                DisplayText = $"{r.Name}  →  {r.Action.Mode}  [{r.Action.Level}]",
                LevelColor = GetLevelBrush(r.Action.Level)
            });
        }
    }

    private static IBrush GetLevelBrush(string level) => level switch
    {
        "job-locked" => JobLockedBrush,
        "job-enforced" => PcoreBrush,
        "hard-affinity" => PcoreSmtBrush,
        "soft-cpu-sets" => EcoreBrush,
        _ => LogicalBrush
    };
}

public partial class CoreVisualItem : ObservableObject
{
    [ObservableProperty] private int _index;
    [ObservableProperty] private IBrush _colorBrush = Brushes.Gray;
    [ObservableProperty] private string _tooltip = "";
}

public partial class RuleSummaryItem : ObservableObject
{
    [ObservableProperty] private string _displayText = "";
    [ObservableProperty] private IBrush _levelColor = Brushes.Gray;
}
