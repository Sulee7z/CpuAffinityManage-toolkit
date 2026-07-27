using Avalonia.Controls;
using Avalonia.Input;
using CpuAffinityManager.Avalonia.ViewModels;
using CpuAffinityManager.Engine;

namespace CpuAffinityManager.Avalonia.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;

    /// <summary>
    /// When true, the next close request really closes the window (used by the tray
    /// "退出" command). Otherwise, with "关闭时最小化到系统托盘" enabled, closing hides
    /// the window to the tray instead of exiting.
    /// </summary>
    public bool ForceExit { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private bool _editorOpen;

    private async void OnOpened(object? sender, System.EventArgs e)
    {
        // 只初始化一次:窗口最小化到托盘后再显示会再次触发 Opened,
        // 之前每次都重复订阅 RuleEditRequested,导致点一次“新建规则”弹出多个窗口。
        Opened -= OnOpened;

        if (DataContext is not MainWindowViewModel vm) return;
        _vm = vm;

        // Handle rule edit dialog requests (re-entrancy guarded)
        vm.RuleEditRequested += async (existing) =>
        {
            if (_editorOpen) return;
            _editorOpen = true;
            try
            {
                var dlg = new RuleEditorWindow(existing);
                var result = await dlg.ShowDialog<RuleEntry?>(this);
                if (result != null)
                {
                    vm.AddOrUpdateRule(result!);
                    vm.RuleList.OnRuleSaved();
                }
            }
            finally { _editorOpen = false; }
        };

        vm.InitializeCommand.Execute(null);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Close-to-tray: hide instead of exit when enabled and this isn't an explicit quit.
        if (!ForceExit && _vm?.MinimizeToTray == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // Real shutdown — release all enforcement so nothing is left restricted.
        try { _vm?.Shutdown(); } catch { }
    }

    /// <summary>Lets the user drag the window by the custom (extended) title bar.</summary>
    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>Restores and focuses the window (invoked from the tray menu).</summary>
    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
}
