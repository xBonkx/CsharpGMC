using System.Windows;

namespace GmcSmeta.App;

public partial class StagesDialog
{
    private void ClearStageSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }
}

public partial class RateTransferDialog
{
    private void ClearRateSearch_Click(object sender, RoutedEventArgs e)
    {
        SourceSearch.Clear();
        SourceSearch.Focus();
    }
}
