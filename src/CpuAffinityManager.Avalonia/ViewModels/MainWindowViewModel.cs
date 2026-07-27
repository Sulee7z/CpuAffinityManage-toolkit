using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;
using CpuAffinityManager.Enforcement;
using CpuAffinityManager.Monitoring;
using Serilog;

namespace CpuAffinityManager.Avalonia.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IRuleEngine _ruleEngine;
    private readonly ICpuTopologyService _topoService;
    private readonly IEnforcementService _enforcementService;
    private readonly IProcessMonitor _processMonitor;
    private readonly AffinityEnforcementWatchdog _watchdog;

    [ObservableProperty] private ViewModelBase _currentPage;
    [ObservableProperty] private string _windowTitle = "CPU 亲和性管理器";
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _sidebarCpuInfo = "检测中…";
    [ObservableProperty] private bool _isDashboardSelected = true;
    [ObservableProperty] private bool _isProcessesSelected;
    [ObservableProperty] private bool _isRulesSelected;
    [ObservableProperty] private bool _isAiSelected;
    [ObservableProperty] private bool _isToolsSelected;
    [ObservableProperty] private bool _isStartupSelected;
    [ObservableProperty] private bool _isSettingsSelected;

    public DashboardViewModel Dashboard { get; }
    public ProcessListViewModel ProcessList { get; }
    public RuleListViewModel RuleList { get; }
    public AiViewModel Ai { get; }
    public SystemToolsViewModel Tools { get; } = new();
    public StartupViewModel Startup { get; } = new();
    public SettingsViewModel Settings { get; }

    public MainWindowViewModel()
    {
        _ruleEngine = new RuleEngine();
        _topoService = new CpuTopologyService();
        _enforcementService = new EnforcementService(_ruleEngine, _topoService);
        _processMonitor = new WmiProcessMonitor();
        _watchdog = new AffinityEnforcementWatchdog(_ruleEngine, _topoService, _enforcementService);

        _apiService = new Services.ApiServerService(
            _ruleEngine, _topoService, _enforcementService,
            () => { try { _ruleEngine.Save(RuleConfigPath.FindDefaultRules()); } catch { } });

        Dashboard = new DashboardViewModel(_ruleEngine, _topoService);
        ProcessList = new ProcessListViewModel(_ruleEngine, _enforcementService, _topoService) { Parent = this };
        RuleList = new RuleListViewModel(_ruleEngine) { Parent = this };
        Ai = new AiViewModel(_ruleEngine, _topoService,
            () => { SaveRules(); Dashboard.Refresh(); RuleList.Refresh(); });
        Settings = new SettingsViewModel(_apiService);

        CurrentPage = Dashboard;
    }

    private readonly Services.ApiServerService _apiService;

    /// <summary>True when "关闭时最小化到系统托盘" is enabled.</summary>
    public bool MinimizeToTray => Settings.MinimizeToTray;

    /// <summary>
    /// Clean shutdown: stop the watchdog and process monitor, then release every Job
    /// Object affinity limit so no process (including Windows system processes) is left
    /// restricted after exit.
    /// </summary>
    public void Shutdown()
    {
        try { _apiService.Stop(); } catch { }
        try { _watchdog.Stop(); } catch { }
        try { _watchdog.Dispose(); } catch { }
        try { _processMonitor.Dispose(); } catch { }
        try { if (OperatingSystem.IsWindows()) ProcOps.SelfMemoryTrimmer.Stop(); } catch { }
        try { _enforcementService.ShutdownCleanup(); } catch { }
        Log.Information("Shutdown cleanup complete");
    }

    [RelayCommand]
    private void Initialize()
    {
        try
        {
            var topo = _topoService.Detect();
            Log.Information("Topology: {Topo}", topo);
            SidebarCpuInfo = $"{topo.TotalLogicalProcessors} threads · {topo.PcoreCount}P + {topo.EcoreCount}E";
            LoadRules();
            Dashboard.Refresh();
            StartProcessMonitor();
            _watchdog.Start();
            if (OperatingSystem.IsWindows())
                ProcOps.SelfMemoryTrimmer.Start(); // 周期性压缩自身内存占用
        }
        catch (Exception ex) { Log.Error(ex, "Init failed"); StatusText = $"错误: {ex.Message}"; }
    }

    private void LoadRules()
    {
        try
        {
            string path = RuleConfigPath.FindDefaultRules();
            if (System.IO.File.Exists(path)) _ruleEngine.Load(path);
            Log.Information("Loaded {N} rules from {Path}", _ruleEngine.Rules.Count, path);
        }
        catch (Exception ex) { Log.Warning(ex, "Load rules failed"); }
    }

    private void SaveRules()
    {
        try { _ruleEngine.Save(RuleConfigPath.FindDefaultRules()); }
        catch (Exception ex) { Log.Error(ex, "Save rules failed"); }
    }

    private void StartProcessMonitor()
    {
        try
        {
            _processMonitor.Start(e =>
            {
                try
                {
                    string? path = EnforcementService.GetProcessPath(e.Pid);
                    if (path == null) return;
                    var rule = _ruleEngine.Match(e.ProcessName, path);
                    if (rule != null)
                    {
                        _enforcementService.Apply(e.Pid, rule, _topoService.Detect());
                        StatusText = $"已自动应用『{rule.Name}』→ {e.ProcessName} (PID {e.Pid})";
                    }
                }
                catch { }
            });
        }
        catch { }
    }

    // ── Rule management (exposed for RuleListViewModel) ──

    public void AddOrUpdateRule(RuleEntry rule)
    {
        _ruleEngine.AddRule(rule);
        SaveRules();
    }

    public void RemoveRule(string ruleId)
    {
        _ruleEngine.RemoveRule(ruleId);
        SaveRules();
    }

    public void NotifyRuleChanged()
    {
        SaveRules();
        Dashboard.Refresh();
    }

    /// <summary>
    /// When a rule is toggled on/off, scan all running processes and apply or relax affinity.
    /// Mirrors the WPF MainWindow.ApplyRuleToggleToRunningProcesses behaviour.
    /// </summary>
    public void ApplyRuleToggleToRunningProcesses(RuleEntry toggledRule, bool enabled)
    {
        Task.Run(() =>
        {
            int affected = 0;
            var topology = _topoService.Detect();

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    int pid = process.Id;
                    if (pid is 0 or 4)
                        continue;

                    string name = process.ProcessName + ".exe";
                    string? path = null;
                    try { path = process.MainModule?.FileName; } catch { }

                    if (!RuleMatchesProcess(toggledRule, name, path ?? ""))
                        continue;

                    if (enabled)
                    {
                        var activeRule = _ruleEngine.Match(name, path ?? "");
                        if (activeRule?.Id == toggledRule.Id &&
                            _enforcementService.Apply(pid, toggledRule, topology))
                        {
                            affected++;
                        }
                    }
                    else
                    {
                        var replacementRule = _ruleEngine.Match(name, path ?? "");
                        bool ok = replacementRule != null
                            ? _enforcementService.Apply(pid, replacementRule, topology)
                            : _enforcementService.Relax(pid, topology);

                        if (ok)
                            affected++;
                    }
                }
                catch
                {
                    // Process may exit or deny access during toggle scan.
                }
                finally
                {
                    try { process.Dispose(); } catch { }
                }
            }

            StatusText = enabled
                ? $"规则『{toggledRule.Name}』已启用 — 应用到 {affected} 个进程"
                : $"规则『{toggledRule.Name}』已禁用 — 已放开 {affected} 个进程";
            ProcessList.Refresh();
        });
    }

    private static bool RuleMatchesProcess(RuleEntry rule, string processName, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(rule.Match.Process) ||
            !Wildcard.Match(processName, rule.Match.Process, ignoreCase: true))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.Match.Path) &&
            !Wildcard.MatchPath(fullPath, rule.Match.Path, ignoreCase: true))
        {
            return false;
        }

        return rule.Match.Exclude == null ||
               !rule.Match.Exclude.Any(pattern =>
                   Wildcard.Match(processName, pattern, ignoreCase: true));
    }

    /// <summary>Called by RuleListViewModel when user wants to add/edit a rule.</summary>
    public void EditRule(RuleEntry? existing)
    {
        RuleEditRequested?.Invoke(existing);
    }

    public event Action<RuleEntry?>? RuleEditRequested;

    // ── Navigation ──

    [RelayCommand] private void NavigateToDashboard() { CurrentPage = Dashboard; SetNav(true, false, false, false, false, false, false); Dashboard.Refresh(); }
    [RelayCommand] private void NavigateToProcesses() { CurrentPage = ProcessList; SetNav(false, true, false, false, false, false, false); ProcessList.Refresh(); }
    [RelayCommand] private void NavigateToRules() { CurrentPage = RuleList; SetNav(false, false, true, false, false, false, false); RuleList.Refresh(); }
    [RelayCommand] private void NavigateToAi() { CurrentPage = Ai; SetNav(false, false, false, true, false, false, false); }
    [RelayCommand] private void NavigateToTools() { CurrentPage = Tools; SetNav(false, false, false, false, true, false, false); }
    [RelayCommand] private void NavigateToStartup() { CurrentPage = Startup; SetNav(false, false, false, false, false, true, false); Startup.Refresh(); }
    [RelayCommand] private void NavigateToSettings() { CurrentPage = Settings; SetNav(false, false, false, false, false, false, true); }

    private void SetNav(bool d, bool p, bool r, bool a, bool t, bool u, bool s) { IsDashboardSelected = d; IsProcessesSelected = p; IsRulesSelected = r; IsAiSelected = a; IsToolsSelected = t; IsStartupSelected = u; IsSettingsSelected = s; }

    [RelayCommand]
    private async Task ScanNowAsync()
    {
        IsScanning = true; StatusText = "正在扫描…";
        int n = await Task.Run(() => _enforcementService.ScanAndEnforce());
        StatusText = $"扫描完成 — 影响了 {n} 个进程";
        IsScanning = false;
    }
}
