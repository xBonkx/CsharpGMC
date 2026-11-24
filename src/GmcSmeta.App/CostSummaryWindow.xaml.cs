using System.Windows;

namespace GmcSmeta.App;

/// <summary>Standalone compatibility host; the same live module is also used in the contract tabs.</summary>
public partial class CostSummaryWindow : Window
{
    public CostSummaryWindow(string connectionString, int contractId, string title, bool readOnly = false)
    {
        InitializeComponent();
        InlinePaneHost.Content = new CostSummaryPane(connectionString, contractId, title, readOnly);
    }
}
