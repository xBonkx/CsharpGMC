using System.Windows;

namespace GmcSmeta.App;

public partial class ActsEditorView
{
    private void ClearPositionSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }
}

public partial class AllContractActsWindow
{
    private void ClearActsSearch_Click(object sender, RoutedEventArgs e)
    {
        ActsSearch.Clear();
        ActsSearch.Focus();
    }
}
