using System.Windows;

namespace GmcSmeta.App;

public partial class EstimateMaterialsPanel
{
    private void ClearMaterialsSearch_Click(object sender, RoutedEventArgs e)
    {
        MaterialsSearch.Clear();
        MaterialsSearch.Focus();
    }
}

public partial class CostSummaryPane
{
    private void ClearCostMaterialSearch_Click(object sender, RoutedEventArgs e)
    {
        MaterialSearch.Clear();
        MaterialSearch.Focus();
    }
}
