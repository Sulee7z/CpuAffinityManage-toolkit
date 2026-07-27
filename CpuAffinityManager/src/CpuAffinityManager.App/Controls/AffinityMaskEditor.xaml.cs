using System.Windows.Controls;

namespace CpuAffinityManager.App.Controls;

/// <summary>
/// Interactive affinity mask editor — a grid of checkboxes representing
/// each logical processor. Supports P-core/E-core color coding.
/// </summary>
public partial class AffinityMaskEditor : UserControl
{
    public AffinityMaskEditor()
    {
        InitializeComponent();
    }
}
