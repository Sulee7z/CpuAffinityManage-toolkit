using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;
using CpuAffinityManager.Enforcement;
using CpuAffinityManager.Monitoring;
using Serilog;

namespace CpuAffinityManager.App;

public partial class MainWindow : Window
{
    private readonly IRuleEngine _ruleEngine;
    private readonly ICpuTopologyService _topoService;
    private readonly IEnforcementService _enforcementService;
    private readonly IProcessMonitor _processMonitor;
    private readonly AffinityEnforcementWatchdog _watchdog;
    private CpuTopology? _topology;
    private bool _isLoaded;
    private List<ProcessListItem> _allProcessItems = new();

    public MainWindow()
    {
        InitializeComponent();
        _ruleEngine = new RuleEngine();
        _topoService = new CpuTopologyService();
        _enforcementService = new EnforcementService(_ruleEngine, _topoService);
        _processMonitor = new WmiProcessMonitor();
        _watchdog = new AffinityEnforcementWatchdog(_ruleEngine, _topoService, _enforcementService);
        Loaded += OnLoaded;
        Closed += (_, _) => CleanupOnExit();
    }

    /// <summary>
    /// Releases the watchdog, process monitor and every Job Object affinity limit on
    /// exit, so no process (including Windows system processes) is left restricted.
    /// </summary>
    private void CleanupOnExit()
    {
        try { _watchdog.Dispose(); } catch { }
        try { _processMonitor.Dispose(); } catch { }
        try { _enforcementService.ShutdownCleanup(); } catch { }
    }

    #region Startup

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        try
        {
            _topology = _topoService.Detect();
            Log.Information("Topology: {Topo}", _topology);
            LoadRules();
            SelectNav("dashboard");
            SafeText(SidebarCpuInfo, $"{_topology.TotalLogicalProcessors} threads · {_topology.PcoreCount}P + {_topology.EcoreCount}E");
            StartProcessMonitor();
            _watchdog.Start();
        }
        catch (Exception ex) { Log.Error(ex, "Startup failed"); TxtStatus.Text = $"错误: {ex.Message}"; }
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

    private void StartProcessMonitor()
    {
        try
        {
            _processMonitor.Start(e =>
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        string? path = EnforcementService.GetProcessPath(e.Pid);
                        if (path == null) return;
                        var rule = _ruleEngine.Match(e.ProcessName, path);
                        if (rule != null)
                        {
                            _enforcementService.Apply(e.Pid, rule, _topoService.Detect());
                            TxtStatus.Text = $"已自动应用『{rule.Name}』→ {e.ProcessName} (PID {e.Pid})";
                        }
                    }
                    catch { }
                });
            });
        }
        catch { }
    }

    #endregion

    #region Navigation

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded) return;
        if (NavList.SelectedItem is ListBoxItem item && item.Tag is string page)
            SelectNav(page);
    }

    private void SelectNav(string page)
    {
        SafeSet(() => DashboardPage.Visibility = page == "dashboard" ? Visibility.Visible : Visibility.Collapsed);
        SafeSet(() => ProcessesPage.Visibility = page == "processes" ? Visibility.Visible : Visibility.Collapsed);
        SafeSet(() => RulesPage.Visibility = page == "rules" ? Visibility.Visible : Visibility.Collapsed);
        SafeSet(() => SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed);

        switch (page)
        {
            case "dashboard": SafeText(PageTitle, "仪表盘"); RefreshDashboard(); break;
            case "processes": SafeText(PageTitle, "进程"); RefreshProcessList(); break;
            case "rules": SafeText(PageTitle, "规则"); RefreshRulesList(); break;
            case "settings": SafeText(PageTitle, "设置"); break;
        }
    }

    #endregion

    #region Dashboard

    private void RefreshDashboard()
    {
        if (_topology == null || !_isLoaded) return;
        try
        {
            SafeText(StatProcessCount, System.Diagnostics.Process.GetProcesses().Length.ToString());
        }
        catch { SafeText(StatProcessCount, "??"); }
        SafeText(StatRulesCount, _ruleEngine.Rules.Count(r => r.Enabled).ToString());
        SafeText(StatPcoreCount, _topology.PcoreCount.ToString());
        SafeText(StatEcoreCount, _topology.EcoreCount.ToString());

        var coreItems = new List<CoreVisualItem>();
        for (int i = 0; i < _topology.TotalLogicalProcessors && i < 64; i++)
        {
            ulong bit = 1UL << i;
            coreItems.Add(new CoreVisualItem
            {
                Index = i,
                ColorBrush = (_topology.PcoreMask & bit) != 0
                    ? ((_topology.Smt1Mask & bit) != 0 ? PcoreSmtBrush : PcoreBrush)
                    : (_topology.EcoreMask & bit) != 0 ? EcoreBrush : LogicalBrush,
                Tooltip = $"LP#{i}"
            });
        }
        CoreVisualList.ItemsSource = coreItems;
        RuleSummaryList.ItemsSource = _ruleEngine.Rules.Where(r => r.Enabled).Select(r =>
            new RuleSummaryItem { DisplayText = $"{r.Name} → {r.Action.Mode} [{r.Action.Level}]", LevelColor = LevelBrush(r.Action.Level) }).ToList();
    }

    #endregion

    #region Process List

    private void ProcessRefresh_Click(object sender, RoutedEventArgs e) => RefreshProcessList();

    private void RefreshProcessList()
    {
        if (!_isLoaded) return;
        TxtStatus.Text = "正在扫描进程…";

        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            var items = new List<ProcessListItem>();
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    try
                    {
                        int pid; string name;
                        try { pid = p.Id; } catch { continue; }
                        try { name = p.ProcessName + ".exe"; } catch { continue; }
                        string? path = null; try { if (pid is not 0 and not 4) path = p.MainModule?.FileName; } catch { }
                        string aff = "N/A"; try { if (pid is not 0 and not 4) aff = $"{p.ProcessorAffinity.ToInt64():X8}"; } catch { }
                        var rule = _ruleEngine.Match(name, path ?? "");
                        items.Add(new ProcessListItem
                        {
                            Pid = pid, Name = name, Path = path ?? "(protected)", AffinityShort = aff,
                            RuleLevelText = rule?.Action.Level ?? "",
                            HasMatchedRule = rule != null
                        });
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch (Exception ex) { Log.Error(ex, "Process enum failed"); }

            Dispatcher.Invoke(() =>
            {
                _allProcessItems = items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
                ApplyProcessFilter();
                TxtStatus.Text = $"已加载 {items.Count} 个进程";
            });
        });
    }

    private void ProcessSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyProcessFilter();

    private void ApplyProcessFilter()
    {
        string filter = (ProcessSearchBox.Text ?? "").Trim().ToLowerInvariant();
        var filtered = string.IsNullOrEmpty(filter)
            ? _allProcessItems
            : _allProcessItems.Where(p => p.Name.ToLowerInvariant().Contains(filter)
                || p.Path.ToLowerInvariant().Contains(filter)
                || p.Pid.ToString().Contains(filter)).ToList();
        ProcessListBox.ItemsSource = filtered;
    }

    #endregion

    #region Process Context Menu Actions

    private ProcessListItem? GetSelectedProcess() => ProcessListBox.SelectedItem as ProcessListItem;

    // Preset table: header text → "mode|level", or "@rule" for the matched-rule action.
    // Kept as data so the whole context menu is driven by ONE Click handler instead
    // of eight near-identical event methods.
    private static readonly (string Header, string Preset)[] ProcessMenuPresets =
    {
        ("设为大核 (P-Core)",                    "p-cores|first-half=hard-affinity"),
        ("设为小核 (E-Core)",                    "e-cores|second-half=hard-affinity"),
        ("设为全部核心",                  "all-cores=hard-affinity"),
        ("-",                                        ""),
        ("设为前一半核心",                 "first-half=hard-affinity"),
        ("设为后一半核心",                "second-half=hard-affinity"),
        ("-",                                        ""),
        ("Job 对象强制 (防篡改)",     "p-cores|all-cores=job-enforced"),
        ("Job 对象锁定 (禁止脱离)",          "all-cores=job-locked"),
        ("-",                                        ""),
        ("应用匹配的规则",                       "@rule"),
    };

    // Built once and reused for every row — the old code rebuilt a fresh menu on
    // each right-click / ContextMenuOpening.
    private ContextMenu? _processContextMenu;

    private ContextMenu GetProcessContextMenu()
    {
        if (_processContextMenu != null)
            return _processContextMenu;

        var menu = new ContextMenu();
        foreach (var (header, preset) in ProcessMenuPresets)
        {
            if (preset.Length == 0) { menu.Items.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = header, Tag = preset };
            mi.Click += ProcessMenuItem_Click;
            menu.Items.Add(mi);
        }
        _processContextMenu = menu;
        return menu;
    }

    private void ProcessListItem_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
            item.ContextMenu = GetProcessContextMenu();
        }
    }

    private void ProcessListItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is ListBoxItem item)
            item.ContextMenu = GetProcessContextMenu();
    }

    private void ProcessMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var target = GetSelectedProcess();
        if (target == null || sender is not MenuItem mi || mi.Tag is not string preset)
            return;

        try
        {
            if (preset == "@rule")
            {
                var matched = _ruleEngine.Match(target.Name, target.Path);
                if (matched != null)
                {
                    _enforcementService.Apply(target.Pid, matched, _topoService.Detect());
                    TxtStatus.Text = $"已对 PID {target.Pid} 应用规则『{matched.Name}』";
                }
                else { TxtStatus.Text = "没有匹配该进程的规则"; }
                return;
            }

            int eq = preset.IndexOf('=');
            string mode = preset.Substring(0, eq);
            string level = preset.Substring(eq + 1);
            var rule = new RuleEntry { Id = "ctx", Name = "Context Menu", Action = new RuleAction { Mode = mode, Level = level } };
            bool ok = _enforcementService.Apply(target.Pid, rule, _topoService.Detect());
            TxtStatus.Text = ok ? $"已对 PID {target.Pid} 应用 {mode} [{level}]" : $"PID {target.Pid} 应用失败";
        }
        catch (Exception ex) { TxtStatus.Text = $"错误: {ex.Message}"; }
    }

    #endregion

    #region Rule Editor

    private void AddRule_Click(object sender, RoutedEventArgs e) => OpenRuleEditor(null);

    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
            OpenRuleEditor(id);
    }

    private void OpenRuleEditor(string? ruleId)
    {
        RuleEntry? edit = null;
        if (ruleId != null)
            edit = _ruleEngine.Rules.FirstOrDefault(r => r.Id == ruleId);

        var dlg = new RuleEditorWindow(edit) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            _ruleEngine.AddRule(dlg.Result);
            SaveRules();
            RefreshRulesList();
            RefreshDashboard();
            TxtStatus.Text = $"规则『{dlg.Result.Name}』已保存";
        }
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        if (MessageBox.Show($"确定删除规则『{id}』吗?", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _ruleEngine.RemoveRule(id);
            SaveRules();
            RefreshRulesList();
            RefreshDashboard();
            TxtStatus.Text = $"规则『{id}』已删除";
        }
    }

    private void RuleEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not string id)
            return;

        var rule = _ruleEngine.Rules.FirstOrDefault(r => r.Id == id);
        if (rule == null)
            return;

        rule.Enabled = checkBox.IsChecked == true;
        SaveRules();
        ApplyRuleToggleToRunningProcesses(rule, rule.Enabled);
        RefreshDashboard();
        TxtStatus.Text = rule.Enabled
            ? $"规则『{rule.Name}』已启用"
            : $"规则『{rule.Name}』已禁用";
    }

    private void ApplyRuleToggleToRunningProcesses(RuleEntry toggledRule, bool enabled)
    {
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            int affected = 0;
            var topology = _topoService.Detect();

            foreach (var process in System.Diagnostics.Process.GetProcesses())
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

            Dispatcher.Invoke(() =>
            {
                TxtStatus.Text = enabled
                    ? $"规则『{toggledRule.Name}』已启用 — 应用到 {affected} 个进程"
                    : $"规则『{toggledRule.Name}』已禁用 — 已放开 {affected} 个进程";
                RefreshProcessList();
            });
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

    private void SaveRules()
    {
        try
        {
            string path = RuleConfigPath.FindDefaultRules();
            _ruleEngine.Save(path);
        }
        catch (Exception ex) { Log.Error(ex, "Save rules failed"); }
    }

    #endregion

    #region Rules List

    private void RefreshRulesList()
    {
        RulesList.ItemsSource = _ruleEngine.Rules.Select(r => new RuleListItem
        {
            Id = r.Id, Name = r.Name, Enabled = r.Enabled,
            ModeDisplay = r.Action.Mode.ToUpper(), LevelText = r.Action.Level,
            MatchDisplay = $"Matches: {r.Match.Process}" + (string.IsNullOrEmpty(r.Match.Path) ? "" : $" in {r.Match.Path}")
        }).ToList();
    }

    #endregion

    #region Scan

    private void ScanNow_Click(object sender, RoutedEventArgs e)
    {
        BtnScanNow.IsEnabled = false;
        BtnScanNow.Content = "⏳ Scanning...";
        TxtStatus.Text = "正在扫描全部进程…";
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            int n = _enforcementService.ScanAndEnforce();
            Dispatcher.Invoke(() =>
            {
                TxtStatus.Text = $"扫描完成 — 影响了 {n} 个进程";
                BtnScanNow.IsEnabled = true;
                BtnScanNow.Content = "🔍  Scan Now";
                RefreshProcessList();
            });
        });
    }

    #endregion

    #region Window Chrome

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object s, RoutedEventArgs e)
    {
        // "关闭最小化": the ✕ button minimizes to the taskbar when enabled, instead of
        // exiting. Use Alt+F4 to actually quit. This keeps enforcement running in the
        // background like the tray behaviour on the Avalonia build.
        if (ChkMinimizeTray?.IsChecked == true)
            WindowState = WindowState.Minimized;
        else
            Close();
    }

    #endregion

    #region Settings

    private void Settings_Changed(object sender, RoutedEventArgs e) { /* Auto-save handled by SaveRules */ }

    #endregion

    #region Helpers

    private void SafeSet(Action a) { if (!_isLoaded) return; try { a(); } catch { } }
    private void SafeText(TextBlock? tb, string val) { if (tb != null) tb.Text = val; }

    // Monet-palette brushes, created and frozen once (frozen brushes render faster
    // in WPF and are safe to share across the whole visual tree).
    private static readonly Brush PcoreBrush    = Frozen(0xC4, 0x68, 0x5E); // 罂粟红
    private static readonly Brush PcoreSmtBrush = Frozen(0xC0, 0x85, 0x52); // 赭石
    private static readonly Brush EcoreBrush    = Frozen(0x6D, 0x9D, 0xC5); // 湖蓝
    private static readonly Brush LogicalBrush  = Frozen(0x9A, 0xA6, 0xAE); // 灰蓝
    private static readonly Brush JobLockedBrush = Frozen(0x8A, 0x4A, 0x44); // 暗红

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Brush LevelBrush(string level) => level switch
    {
        "job-locked" => JobLockedBrush,
        "job-enforced" => PcoreBrush,
        "hard-affinity" => PcoreSmtBrush,
        _ => EcoreBrush
    };

    #endregion
}

#region ViewModels

public class CoreVisualItem { public int Index { get; set; } public Brush ColorBrush { get; set; } = Brushes.Gray; public string Tooltip { get; set; } = ""; }
public class RuleSummaryItem { public string DisplayText { get; set; } = ""; public Brush LevelColor { get; set; } = Brushes.Gray; }
public class ProcessListItem { public string Name { get; set; } = ""; public int Pid { get; set; } public string Path { get; set; } = ""; public string AffinityShort { get; set; } = ""; public string RuleLevelText { get; set; } = ""; public bool HasMatchedRule { get; set; } }
public class RuleListItem { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public bool Enabled { get; set; } public string ModeDisplay { get; set; } = ""; public string LevelText { get; set; } = ""; public string MatchDisplay { get; set; } = ""; }

#endregion
