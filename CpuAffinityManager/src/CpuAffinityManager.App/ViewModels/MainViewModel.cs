using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;
using CpuAffinityManager.Enforcement;
using CpuAffinityManager.Monitoring;

namespace CpuAffinityManager.App.ViewModels;

/// <summary>
/// Main ViewModel binding the core engine to the WPF UI.
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private readonly IRuleEngine _ruleEngine;
    private readonly ICpuTopologyService _topoService;
    private readonly IEnforcementService _enforcementService;
    private readonly IProcessMonitor _processMonitor;

    private string _statusText = "Ready";
    private bool _wmiMonitorEnabled = true;

    public MainViewModel(
        IRuleEngine ruleEngine,
        ICpuTopologyService topoService,
        IEnforcementService enforcementService,
        IProcessMonitor processMonitor)
    {
        _ruleEngine = ruleEngine;
        _topoService = topoService;
        _enforcementService = enforcementService;
        _processMonitor = processMonitor;
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public bool WmiMonitorEnabled
    {
        get => _wmiMonitorEnabled;
        set
        {
            _wmiMonitorEnabled = value;
            OnPropertyChanged();
            if (value)
                StartMonitor();
            else
                _processMonitor.Stop();
        }
    }

    public IReadOnlyList<RuleEntry> Rules => _ruleEngine.Rules;
    public CpuTopology? Topology { get; private set; }

    public void Initialize()
    {
        Topology = _topoService.Detect();
        if (WmiMonitorEnabled)
            StartMonitor();
    }

    public void ScanAndEnforce()
    {
        StatusText = "Scanning...";
        int count = _enforcementService.ScanAndEnforce();
        StatusText = $"Applied rules to {count} process(es)";
    }

    private void StartMonitor()
    {
        _processMonitor.Start(e =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                string? fullPath = EnforcementService.GetProcessPath(e.Pid);
                if (fullPath == null) return;

                var rule = _ruleEngine.Match(e.ProcessName, fullPath);
                if (rule != null)
                {
                    _enforcementService.Apply(e.Pid, rule, _topoService.Detect());
                    StatusText = $"Auto-applied '{rule.Name}' to {e.ProcessName} (PID {e.Pid})";
                }
            });
        });
        StatusText = "WMI monitor active";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
