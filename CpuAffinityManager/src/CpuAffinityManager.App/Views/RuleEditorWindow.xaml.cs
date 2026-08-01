using System.Windows;
using CpuAffinityManager.Engine;

namespace CpuAffinityManager.App;

public partial class RuleEditorWindow : Window
{
    public RuleEntry? Result { get; private set; }
    private readonly RuleEntry? _editTarget;

    public RuleEditorWindow(RuleEntry? editTarget = null)
    {
        InitializeComponent();
        _editTarget = editTarget;

        if (editTarget != null)
        {
            // Edit mode
            DlgTitle.Text = "编辑规则";
            BtnSave.Content = "更新规则";
            TxtName.Text = editTarget.Name;
            TxtProcess.Text = editTarget.Match.Process;
            TxtPath.Text = editTarget.Match.Path ?? "";
            CmbMode.Text = editTarget.Action.Mode;
            CmbLevel.Text = editTarget.Action.Level;
            TxtCustomMask.Text = editTarget.Action.CustomMask ?? "";
            TxtSocket.Text = (editTarget.Action.SocketIndex ?? -1).ToString();
            ChkLock.IsChecked = editTarget.Action.Lock;
            ChkEnabled.IsChecked = editTarget.Enabled;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string name = TxtName.Text.Trim();
        string process = TxtProcess.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(process))
        {
            MessageBox.Show("Rule name and process pattern are required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int? socketIdx = null;
        if (int.TryParse(TxtSocket.Text.Trim(), out int si) && si >= 0)
            socketIdx = si;

        Result = new RuleEntry
        {
            Id = _editTarget?.Id ?? $"rule-{Guid.NewGuid():N}"[..8],
            Name = name,
            Enabled = ChkEnabled.IsChecked == true,
            Match = new RuleMatch
            {
                Process = process,
                Path = string.IsNullOrWhiteSpace(TxtPath.Text) ? null : TxtPath.Text.Trim()
            },
            Action = new RuleAction
            {
                Mode = (CmbMode.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "all-cores",
                Level = (CmbLevel.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "hard-affinity",
                CustomMask = string.IsNullOrWhiteSpace(TxtCustomMask.Text) ? null : TxtCustomMask.Text.Trim(),
                SocketIndex = socketIdx,
                Lock = ChkLock.IsChecked == true
            }
        };

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
