using System.ComponentModel;
using System.Runtime.CompilerServices;
using CpuAffinityManager.Engine;

namespace CpuAffinityManager.App.ViewModels;

/// <summary>
/// ViewModel for the rule editor dialog.
/// </summary>
public class RuleEditorViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _processPattern = string.Empty;
    private string _pathPattern = string.Empty;
    private string _mode = "all-cores";
    private string _level = "hard-affinity";
    private bool _enabled = true;
    private bool _lockBreakaway;

    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string ProcessPattern { get => _processPattern; set { _processPattern = value; OnPropertyChanged(); } }
    public string PathPattern { get => _pathPattern; set { _pathPattern = value; OnPropertyChanged(); } }
    public string Mode { get => _mode; set { _mode = value; OnPropertyChanged(); } }
    public string Level { get => _level; set { _level = value; OnPropertyChanged(); } }
    public bool Enabled { get => _enabled; set { _enabled = value; OnPropertyChanged(); } }
    public bool LockBreakaway { get => _lockBreakaway; set { _lockBreakaway = value; OnPropertyChanged(); } }

    /// <summary>
    /// Available affinity modes for the dropdown.
    /// </summary>
    public static IReadOnlyList<string> AvailableModes { get; } = new[]
    {
        "all-cores", "p-cores", "e-cores", "p-cores-smt",
        "p-cores-no-smt", "first-half", "second-half", "custom"
    };

    /// <summary>
    /// Available enforcement levels for the dropdown.
    /// </summary>
    public static IReadOnlyList<string> AvailableLevels { get; } = new[]
    {
        "soft-cpu-sets", "hard-affinity", "job-enforced", "job-locked"
    };

    /// <summary>
    /// Creates a RuleEntry from the current editor state.
    /// </summary>
    public RuleEntry ToRuleEntry()
    {
        return new RuleEntry
        {
            Id = $"rule-{Guid.NewGuid():N}"[..8],
            Name = Name,
            Enabled = Enabled,
            Match = new RuleMatch
            {
                Process = ProcessPattern,
                Path = string.IsNullOrWhiteSpace(PathPattern) ? null : PathPattern
            },
            Action = new RuleAction
            {
                Type = "cpu-affinity",
                Mode = Mode,
                Level = Level,
                Lock = LockBreakaway
            }
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
