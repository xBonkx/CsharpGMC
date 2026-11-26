using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using GmcSmeta.App;

internal static class LazyEstimateSummaryChecks
{
    internal static void Run()
    {
        var host = new Border { Visibility = Visibility.Collapsed };
        var first = EstimateView.EnsureSummaryView(host, null);
        if (!ReferenceEquals(host.Child, first) ||
            first.FindName("SummaryGrid") is not DataGrid grid ||
            grid.Columns.Count != 19 || grid.IsReadOnly ||
            !grid.EnableRowVirtualization || !grid.EnableColumnVirtualization)
            throw new Exception("Lazy summary grid lost editing, columns or virtualization.");
        if (first.FindName("SummarySearch") is not TextBox ||
            first.FindName("SummaryMissingToggle") is not ToggleButton ||
            first.FindName("SummarySystemFilter") is not Button ||
            first.FindName("SummaryTileMatValue") is not TextBlock ||
            first.FindName("SummaryTransferSmrButton") is not Button ||
            first.FindName("SummaryExcelButton") is not Button)
            throw new Exception("Missing named summary controls.");
        if (grid.Parent is not Grid frameContents ||
            frameContents.Parent is not SummaryTableFrame ||
            frameContents.Children.OfType<SummaryHeaderBands>().SingleOrDefault() is not { } bands ||
            BindingOperations.GetBindingExpression(bands, SummaryHeaderBands.SourceGridProperty)
                ?.ParentBinding.ElementName != "SummaryGrid")
            throw new Exception("Summary frame or two-tier header binding changed.");
        host.Visibility = Visibility.Visible;
        host.Visibility = Visibility.Collapsed;
        var second = EstimateView.EnsureSummaryView(host, null);
        if (!ReferenceEquals(first, second) || !ReferenceEquals(grid, second.FindName("SummaryGrid")))
            throw new Exception("Returning to summary must retain the existing visual tree.");
        Console.WriteLine("Lazy summary panel construction and reuse OK (no SQL).");
    }
}
