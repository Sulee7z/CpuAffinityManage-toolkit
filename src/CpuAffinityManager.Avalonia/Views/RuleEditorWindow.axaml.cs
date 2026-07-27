using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;

namespace CpuAffinityManager.Avalonia.Views;

public partial class RuleEditorWindow : Window
{
    public RuleEntry? Result { get; private set; }
    private readonly RuleEntry? _editTarget;
    private readonly List<CheckBox> _coreBoxes = new();
    private ulong _pMask;

    public RuleEditorWindow() : this(null) { }

    public RuleEditorWindow(RuleEntry? editTarget)
    {
        InitializeComponent();
        _editTarget = editTarget;
        BuildCoreBoxes();

        if (editTarget != null)
        {
            DlgTitle.Text = "编辑规则";
            BtnSave.Content = "更新规则";
            TxtName.Text = editTarget.Name;
            TxtProcess.Text = editTarget.Match.Process;
            TxtPath.Text = editTarget.Match.Path ?? "";
            SetCombo(CmbMode, editTarget.Action.Mode);
            SetCombo(CmbLevel, editTarget.Action.Level);
            ChkEnabled.IsChecked = editTarget.Enabled;
            ChkLock.IsChecked = editTarget.Action.Lock;

            // 优先核心回填:custom+customMask = 硬锁定;preferredCores = 优先调度。
            if (editTarget.Action.Mode == "custom" &&
                editTarget.Action.GetCustomMask() is ulong cm && cm != 0)
            {
                CmbPreferStrength.SelectedIndex = 1;
                for (int i = 0; i < _coreBoxes.Count; i++)
                    _coreBoxes[i].IsChecked = (cm & (1UL << i)) != 0;
            }
            else
            {
                ulong prefer = editTarget.Action.GetPreferredMask();
                for (int i = 0; i < _coreBoxes.Count; i++)
                    _coreBoxes[i].IsChecked = (prefer & (1UL << i)) != 0;
            }
        }
    }

    /// <summary>按本机实际逻辑核生成“核心 N”复选框(标注 P 大核 / E 小核)。</summary>
    private void BuildCoreBoxes()
    {
        int total = Environment.ProcessorCount;
        ulong pMask = 0, eMask = 0;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var topo = new CpuTopologyService().Detect();
                if (topo.TotalLogicalProcessors > 0) total = topo.TotalLogicalProcessors;
                pMask = topo.PcoreMask;
                eMask = topo.EcoreMask;
            }
        }
        catch { }
        if (total < 1) total = 1;
        if (total > 64) total = 64;
        _pMask = pMask;
        BtnPreferP.IsVisible = pMask != 0;

        for (int i = 0; i < total; i++)
        {
            ulong bit = 1UL << i;
            string tag = (pMask & bit) != 0 ? " (P)" : (eMask & bit) != 0 ? " (E)" : "";
            var cb = new CheckBox
            {
                Content = $"核心 {i}{tag}",
                Width = 104,
                FontSize = 12,
                Margin = new global::Avalonia.Thickness(0, 0, 4, 2)
            };
            _coreBoxes.Add(cb);
            CoreWrap.Children.Add(cb);
        }
    }

    private void PreferAllP_Click(object? sender, RoutedEventArgs e)
    {
        if (_pMask == 0) return;
        for (int i = 0; i < _coreBoxes.Count; i++)
            _coreBoxes[i].IsChecked = (_pMask & (1UL << i)) != 0;
    }

    private void PreferClear_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var cb in _coreBoxes) cb.IsChecked = false;
    }

    private static void SetCombo(ComboBox cb, string value)
    {
        foreach (var item in cb.Items)
            if (item is ComboBoxItem cbi && cbi.Content?.ToString() == value)
                { cb.SelectedItem = cbi; return; }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        string name = TxtName.Text?.Trim() ?? "";
        string process = TxtProcess.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(process)) return;

        ulong prefer = 0;
        for (int i = 0; i < _coreBoxes.Count; i++)
            if (_coreBoxes[i].IsChecked == true) prefer |= 1UL << i;

        string mode = (CmbMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "all-cores";
        string level = (CmbLevel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "hard-affinity";
        bool hardLock = prefer != 0 && CmbPreferStrength.SelectedIndex == 1;

        if (hardLock)
        {
            // 硬锁定:直接把勾选核心作为 custom 亲和性掩码,守护线程每秒重申。
            mode = "custom";
            if (level == "soft-cpu-sets") level = "hard-affinity"; // 软级别锁不住,至少硬亲和
        }

        Result = new RuleEntry
        {
            Id = _editTarget?.Id ?? $"rule-{Guid.NewGuid():N}"[..8],
            Name = name,
            Enabled = ChkEnabled.IsChecked == true,
            Match = new RuleMatch { Process = process, Path = string.IsNullOrWhiteSpace(TxtPath.Text) ? null : TxtPath.Text?.Trim() },
            Action = new RuleAction
            {
                Mode = mode,
                Level = level,
                Lock = ChkLock.IsChecked == true,
                CustomMask = hardLock ? "0x" + prefer.ToString("X") : _editTarget?.Action.CustomMask,
                PreferredCores = (prefer == 0 || hardLock) ? null : "0x" + prefer.ToString("X"),
                PreferredCore = null // legacy field superseded by the mask
            }
        };
        Close(Result);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
