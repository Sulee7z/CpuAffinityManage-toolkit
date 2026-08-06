using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CpuAffinityManager.Avalonia.Views;

/// <summary>Result of the core picker. Reset=true means "还原全部核心".</summary>
public sealed class CoreSelectResult
{
    public ulong Mask { get; init; }
    public bool HardLock { get; init; }
    public bool Persist { get; init; }
    public bool Reset { get; init; }
}

/// <summary>
/// Multi-select CPU core picker. Shows one checkbox per logical core (tagged P/E),
/// quick 全选 / 清空 / 仅大核 / 仅小核 buttons, and a 硬锁 option. Returns via
/// ShowDialog&lt;CoreSelectResult?&gt;: a result with the chosen mask (+ HardLock) on 应用,
/// a Reset result on 还原, or null when cancelled.
/// Built entirely in code so it needs no XAML / compiled-binding plumbing.
/// </summary>
public sealed class CoreSelectDialog : Window
{
    private readonly List<CheckBox> _boxes = new();
    private readonly CheckBox _hardLock;
    private readonly CheckBox _persist;
    private readonly ulong _all;

    public CoreSelectDialog(string title, int total, ulong pMask, ulong eMask, ulong currentMask, bool persisted = false, bool simple = false)
    {
        if (total <= 0) total = 1;
        if (total > 64) total = 64;
        _all = total >= 64 ? ~0UL : (1UL << total) - 1;

        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        root.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15 });
        root.Children.Add(new TextBlock
        {
            Text = simple
                ? "勾选优先核心(可多选):进程仍可用全部核心,但单线程/主线程负载会优先跑在勾选的核心上。"
                : "勾选希望该进程运行的 CPU 核心(可多选),然后点“应用”。P=大核,E=小核。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = 0.7
        });

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < total; i++)
        {
            ulong bit = 1UL << i;
            string tag = (pMask & bit) != 0 ? " (P)" : (eMask & bit) != 0 ? " (E)" : "";
            var cb = new CheckBox
            {
                Content = $"核心 {i}{tag}",
                Width = 135,
                Margin = new Thickness(0, 0, 4, 2),
                IsChecked = (currentMask & bit) != 0
            };
            _boxes.Add(cb);
            wrap.Children.Add(cb);
        }
        root.Children.Add(new ScrollViewer { MaxHeight = 340, Content = wrap });

        // Quick selectors
        var quick = new WrapPanel { Orientation = Orientation.Horizontal };
        quick.Children.Add(Btn("全选", () => SetAll(true)));
        quick.Children.Add(Btn("清空", () => SetAll(false)));
        if (pMask != 0) quick.Children.Add(Btn("仅大核", () => SetMask(pMask)));
        if (eMask != 0) quick.Children.Add(Btn("仅小核", () => SetMask(eMask)));
        root.Children.Add(quick);

        // Hard-lock option
        _hardLock = new CheckBox
        {
            Content = "🔒 硬锁(用 Job 对象锁定,进程/规则都无法改回;重启该进程才解除)",
            IsChecked = false
        };
        _persist = new CheckBox
        {
            Content = "💾 重启软件后仍然保留(记住此程序,下次启动或新开实例自动套用)",
            IsChecked = persisted
        };
        if (!simple)
        {
            root.Children.Add(_hardLock);
            root.Children.Add(_persist);
        }

        // Actions
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(Btn("还原(全部核心)", () => Close(new CoreSelectResult { Reset = true })));
        actions.Children.Add(Btn("取消", () => Close((CoreSelectResult?)null)));
        var apply = Btn("应用", () =>
        {
            ulong m = 0;
            for (int i = 0; i < _boxes.Count; i++)
                if (_boxes[i].IsChecked == true) m |= 1UL << i;
            Close(new CoreSelectResult { Mask = m, HardLock = _hardLock.IsChecked == true, Persist = _persist.IsChecked == true });
        });
        apply.MinWidth = 72;
        actions.Children.Add(apply);
        root.Children.Add(actions);

        Content = root;
    }

    private Button Btn(string text, Action onClick)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void SetAll(bool value) { foreach (var b in _boxes) b.IsChecked = value; }
    private void SetMask(ulong mask) { for (int i = 0; i < _boxes.Count; i++) _boxes[i].IsChecked = (mask & (1UL << i)) != 0; }
}
