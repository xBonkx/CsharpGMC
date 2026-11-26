using System.IO;
using System.Windows.Controls;
using System.Xml.Linq;
using GmcSmeta.App;

internal static class LazyEstimateTotalsChecks
{
    internal static void Run()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/GmcSmeta.App/EstimateView.xaml")))
            root = root.Parent;
        if (root is null) throw new FileNotFoundException("Cannot find EstimateView.xaml");
        var document = XDocument.Load(Path.Combine(root.FullName, "src/GmcSmeta.App/EstimateView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var host = document.Descendants(presentation + "Border")
            .Single(e => (string?)e.Attribute(xaml + "Name") == "TotalsPanel");
        if (host.Elements().Any() || (string?)host.Attribute("Visibility") != "Collapsed")
            throw new Exception("Estimate eagerly creates totals panel.");

        // Actual compiled XAML, including resource lookup, names and columns.
        var view = new EstimateTotalsPanel();
        if (view.FindName("TotalsGrid") is not DataGrid grid ||
            !grid.Columns.Select(c => c.SortMemberPath).SequenceEqual(
                new[] { "name", "cost", "sell", "margin", "pct" }) ||
            grid.SelectionUnit != DataGridSelectionUnit.Cell || grid.IsReadOnly ||
            !grid.Columns[0].IsReadOnly || grid.Columns[1].IsReadOnly || grid.Columns[2].IsReadOnly)
            throw new Exception("Lazy totals grid differs from the original editing layout.");
        foreach (var name in new[] { "TotalsTileCostValue", "TotalsTileSellCaption",
                     "TotalsTileSellValue", "TotalsTileMarginValue" })
            if (view.FindName(name) is not TextBlock)
                throw new Exception("Missing totals tile: " + name);
        Console.WriteLine("Lazy estimate totals visual panel OK (no SQL).");
    }
}
