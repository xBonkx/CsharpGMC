using System.Windows;

namespace GmcSmeta.App;

/// <summary>Standalone compatibility host; the same live module is also used in the contract tabs.</summary>
public partial class ProcurementWindow : Window
{
    public ProcurementWindow(string connectionString, int contractId, string title)
    {
        InitializeComponent();
        InlinePaneHost.Content = new ProcurementPane(connectionString, contractId, title);
    }
}
