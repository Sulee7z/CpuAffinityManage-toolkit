using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CpuAffinityManager.Engine;

namespace CpuAffinityManager.Avalonia.ViewModels;

public partial class RuleListViewModel : ViewModelBase
{
    private readonly IRuleEngine _ruleEngine;

    [ObservableProperty] private bool _hasRules;
    public ObservableCollection<RuleItem> Rules { get; } = new();
    public static string[] AvailableModes { get; } = ["all-cores", "p-cores", "e-cores", "p-cores-smt", "p-cores-no-smt", "first-half", "second-half", "custom"];
    public static string[] AvailableLevels { get; } = ["soft-cpu-sets", "hard-affinity", "job-enforced", "job-locked"];

    public MainWindowViewModel? Parent { get; set; }

    public RuleListViewModel(IRuleEngine ruleEngine) { _ruleEngine = ruleEngine; }

    public void Refresh()
    {
        Rules.Clear();
        foreach (var r in _ruleEngine.Rules)
            Rules.Add(new RuleItem { Id = r.Id, Name = r.Name, Enabled = r.Enabled, ProcessPattern = r.Match.Process, PathPattern = r.Match.Path ?? "", Mode = r.Action.Mode, Level = r.Action.Level, LockBreakaway = r.Action.Lock, OnToggled = HandleRuleToggled });
        HasRules = Rules.Count > 0;
    }

    private void HandleRuleToggled(RuleItem item, bool enabled)
    {
        var rule = _ruleEngine.Rules.FirstOrDefault(r => r.Id == item.Id);
        if (rule == null) return;

        // Skip if already in the desired state (e.g. during Refresh / initial load)
        if (rule.Enabled == enabled) return;

        rule.Enabled = enabled;
        Parent?.NotifyRuleChanged();
        Parent?.ApplyRuleToggleToRunningProcesses(rule, enabled);
        if (Parent != null)
            Parent.StatusText = enabled
                ? $"规则『{rule.Name}』已启用"
                : $"规则『{rule.Name}』已禁用";
    }

    [RelayCommand]
    private void ToggleRule(RuleItem item)
    {
        var rule = _ruleEngine.Rules.FirstOrDefault(r => r.Id == item.Id);
        if (rule != null) { rule.Enabled = !rule.Enabled; Parent?.NotifyRuleChanged(); Refresh(); }
    }

    [RelayCommand]
    private void EditRule(RuleItem item)
    {
        // Let MainWindowViewModel handle dialog
        var rule = _ruleEngine.Rules.FirstOrDefault(r => r.Id == item.Id);
        if (rule != null) Parent?.EditRule(rule);
    }

    [RelayCommand]
    private void DeleteRule(RuleItem item)
    {
        Parent?.RemoveRule(item.Id);
        Refresh();
        Parent?.Dashboard.Refresh();
    }

    [RelayCommand]
    private void AddRule()
    {
        Parent?.EditRule(null);
    }

    public void OnRuleSaved() { Refresh(); Parent?.Dashboard.Refresh(); }

    // ── 导出 / 导入规则模板 ──

    private static Window? MainWindow()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static FilePickerFileType JsonType => new("规则 JSON") { Patterns = new[] { "*.json" } };

    [RelayCommand]
    private async Task ExportRules()
    {
        var sp = MainWindow()?.StorageProvider;
        if (sp == null) return;
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出规则",
            SuggestedFileName = "cpu-rules.json",
            FileTypeChoices = new[] { JsonType }
        });
        if (file == null) return;
        try
        {
            _ruleEngine.Save(file.Path.LocalPath);
            if (Parent != null) Parent.StatusText = "规则已导出:" + file.Name;
        }
        catch (Exception ex) { if (Parent != null) Parent.StatusText = "导出失败:" + ex.Message; }
    }

    [RelayCommand]
    private async Task ImportRules()
    {
        var sp = MainWindow()?.StorageProvider;
        if (sp == null) return;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入规则",
            AllowMultiple = false,
            FileTypeFilter = new[] { JsonType }
        });
        if (files.Count == 0) return;
        try
        {
            _ruleEngine.Load(files[0].Path.LocalPath);
            Refresh();
            Parent?.NotifyRuleChanged();       // persist to live config + refresh dashboard
            if (Parent != null) Parent.StatusText = $"已导入 {Rules.Count} 条规则";
        }
        catch (Exception ex) { if (Parent != null) Parent.StatusText = "导入失败:" + ex.Message; }
    }
}

public partial class RuleItem : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _processPattern = "";
    [ObservableProperty] private string _pathPattern = "";
    [ObservableProperty] private string _mode = "";
    [ObservableProperty] private string _level = "";
    [ObservableProperty] private bool _lockBreakaway;

    /// <summary>Callback invoked when the user toggles the enable/disable switch.</summary>
    public Action<RuleItem, bool>? OnToggled { get; set; }

    partial void OnEnabledChanged(bool value)
    {
        OnToggled?.Invoke(this, value);
    }
}
