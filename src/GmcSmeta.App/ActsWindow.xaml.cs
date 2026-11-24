using System.Windows;

namespace GmcSmeta.App;

/// <summary>Compatibility window. The same working editor is also embedded in contract tabs.</summary>
public partial class ActsWindow : Window
{
    public ActsWindow(string connectionString, int contractId, string title, long? actId = null)
    {
        InitializeComponent();
        Editor.ShowActSelector(true);
        Editor.ShowListToggle(false);
        Editor.Bind(connectionString, contractId, title, actId);
    }
}
