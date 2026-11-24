using System.Windows;

namespace GmcSmeta.App;

/// <summary>Standalone compatibility host; the same live module is also used in the contract tabs.</summary>
public partial class ShipmentsWindow : Window
{
    public ShipmentsWindow(string connectionString, int objectId, string objectName)
    {
        InitializeComponent();
        InlinePaneHost.Content = new ShipmentsPane(connectionString, objectId, objectName);
    }
}
