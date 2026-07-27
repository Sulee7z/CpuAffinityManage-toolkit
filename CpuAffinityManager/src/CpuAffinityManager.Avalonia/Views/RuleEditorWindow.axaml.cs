using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using CpuAffinityManager.Cpu;
using CpuAffinityManager.Engine;

namespace CpuAffinityManager.Avalonia.Views;

public partial class RuleEditorWindow : Window
{
    public RuleEntry? Result { get; private set; }
    private readonly RuleEntry? _editTarget;
    private readonly List<CheckBox> _poolBoxes = new();
    private readonly List<CheckBox> _priorityBoxes = new();
    private ulong _pMask;

    public RuleEditorWindow() : this(null) { }

    public RuleEditorWindow(RuleEntry? editTarget)
    {
        InitializeComponent();
        _editTarget = editTarget;
        BuildCoreGrid();

        CmbLevel.SelectionChanged += (_, _) => UpdateLockedHint();

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

            int gpuIdx = (editTarget.Action.GpuPriority ?? -1) + 1;
            if (gpuIdx >= 0 && gpuIdx < CmbGpuPriority.Items.Count)
                CmbGpuPriority.SelectedIndex = gpuIdx;

            string pm = editTarget.Action.GetPreferMode();
            if (pm == "static") CmbPreferMode.SelectedIndex = 1;
            else if (pm == "d2") CmbPreferMode.SelectedIndex = 2;
            else if (pm == "d3") CmbPreferMode.SelectedIndex = 3;
            else CmbPreferMode.SelectedIndex = 0;

            ulong pool = editTarget.Action.GetSchedulingPoolMask();
            ulong prefer = editTarget.Action.GetPreferredMask();
            for (int i = 0; i < _poolBoxes.Count; i++)
            {
                ulong bit = 1UL << i;
                if (pool != 0)
                    _poolBoxes[i].IsChecked = (pool & bit) != 0;
                else
                    _poolBoxes[i].IsChecked = true;
                _priorityBoxes[i].IsChecked = (prefer & bit) != 0;
            }
            UpdateLockedHint();
        }
    }

    private void BuildCoreGrid()
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

        for (int i = 0; i < total; i++)
        {
            int idx = i;
            ulong bit = 1UL << i;
            string tag = (pMask & bit) != 0 ? " (P)" : (eMask & bit) != 0 ? " (E)" : "";

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("100,40,40"),
                Margin = new global::Avalonia.Thickness(0, 1)
            };

            var label = new TextBlock
            {
                Text = $"核心 {i}{tag}",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var poolCb = new CheckBox
            {
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            poolCb.IsCheckedChanged += (_, _) =>
            {
                if (poolCb.IsChecked != true)
                    _priorityBoxes[idx].IsChecked = false;
            };
            Grid.SetColumn(poolCb, 1);
            row.Children.Add(poolCb);
            _poolBoxes.Add(poolCb);

            var prioCb = new CheckBox
            {
                IsChecked = false,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            prioCb.IsCheckedChanged += (_, _) =>
            {
                if (prioCb.IsChecked == true && _poolBoxes[idx].IsChecked != true)
                    _poolBoxes[idx].IsChecked = true;
            };
            Grid.SetColumn(prioCb, 2);
            row.Children.Add(prioCb);
            _priorityBoxes.Add(prioCb);

            CoreGrid.Children.Add(row);
        }
    }

    private void PreferAllP_Click(object? sender, RoutedEventArgs e)
    {
        if (_pMask == 0) return;
        for (int i = 0; i < _poolBoxes.Count; i++)
        {
            bool isP = (_pMask & (1UL << i)) != 0;
            _poolBoxes[i].IsChecked = isP;
            _priorityBoxes[i].IsChecked = isP;
        }
    }

    private void PreferClear_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var cb in _poolBoxes) cb.IsChecked = false;
        foreach (var cb in _priorityBoxes) cb.IsChecked = false;
    }

    private static void SetCombo(ComboBox cb, string value)
    {
        foreach (var item in cb.Items)
            if (item is ComboBoxItem cbi && cbi.Content?.ToString() == value)
                { cb.SelectedItem = cbi; return; }
    }

    private void UpdateLockedHint()
    {
        string level = (CmbLevel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        if (level is "job-enforced" or "job-locked")
            TxtLockedHint.Text = "已锁定 — Job 对象强制,进程无法自行修改亲和性";
        else if (level == "hard-affinity")
            TxtLockedHint.Text = "半锁定 — 看门狗每秒重申亲和性,进程可能短暂修改";
        else
            TxtLockedHint.Text = "未锁定 — 软偏好,进程可自由使用所有可用核心";
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        string name = TxtName.Text?.Trim() ?? "";
        string process = TxtProcess.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(process)) return;

        ulong pool = 0;
        for (int i = 0; i < _poolBoxes.Count; i++)
            if (_poolBoxes[i].IsChecked == true) pool |= 1UL << i;

        ulong prefer = 0;
        for (int i = 0; i < _priorityBoxes.Count; i++)
            if (_priorityBoxes[i].IsChecked == true) prefer |= 1UL << i;

        prefer &= pool;

        string mode = (CmbMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "all-cores";
        string level = (CmbLevel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "hard-affinity";

        int pmIdx = CmbPreferMode.SelectedIndex;
        string preferMode = pmIdx switch
        {
            1 => "static",
            2 => "d2",
            3 => "d3",
            _ => "dynamic"
        };

        ulong allMask = totalCoreMask();
        bool poolIsAll = (pool & allMask) == allMask;

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
                CustomMask = _editTarget?.Action.CustomMask,
                SchedulingPool = poolIsAll ? null : "0x" + pool.ToString("X"),
                PreferredCores = prefer == 0 ? null : "0x" + prefer.ToString("X"),
                PreferredCore = null,
                PreferMode = preferMode == "dynamic" ? null : preferMode,
                GpuPriority = CmbGpuPriority.SelectedIndex <= 0 ? null : CmbGpuPriority.SelectedIndex - 1
            }
        };
        Close(Result);
    }

    private ulong totalCoreMask()
    {
        int n = _poolBoxes.Count;
        return n >= 64 ? ~0UL : (1UL << n) - 1;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}