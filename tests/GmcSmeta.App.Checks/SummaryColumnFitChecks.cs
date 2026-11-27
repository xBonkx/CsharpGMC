using System.Windows;
using System.Windows.Controls;
using GmcSmeta.App;
using GmcSmeta.Core.Domain;

internal static class SummaryColumnFitChecks
{
    internal static void Run()
    {
        string[] keys = ["name", "unit", "qty", "m_ucost", "m_uprice", "m_mcost", "m_msell",
            "w_ucost", "w_usell", "w_wcost", "w_wsell", "markup", "t_cost", "t_sell",
            "mat_margin", "work_margin"];
        var inputs = keys.Select(k => new EstimateColumnLayout.Column(k,
            SummaryColumnFit.DefaultPreferred(k),
            k switch { "name" => 160, "unit" => 36, "qty" => 44, "markup" => 54,
                "mat_margin" or "work_margin" => 72, _ => 64 },
            k == "name" ? EstimateColumnLayout.Role.Name : EstimateColumnLayout.Role.Fixed)).ToArray();

        foreach (var dpi in new[] { 1d, 1.25, 1.5 })
        foreach (var width in new[] { 1300d, 1450d, 1800d })
        {
            var fit = SummaryColumnFit.Calculate(inputs, width, dpi);
            Require(Math.Abs(fit.Values.Sum() - (Math.Floor(width * dpi) - 1) / dpi) < .001,
                "Summary does not fill the viewport.");
            Require(inputs.All(c => fit[c.Key] >= c.Minimum), "Readable minimum was lost.");
        }
        var hidden = inputs.Where(c => c.Key != "work_margin").ToArray();
        var reduced = SummaryColumnFit.Calculate(hidden, 1300, 1);
        Require(!reduced.ContainsKey("work_margin") && Math.Abs(reduced.Values.Sum() - 1299) < .001,
            "Hidden column consumed space.");
        var narrow = SummaryColumnFit.Calculate(inputs, 500, 1);
        Require(narrow.Values.Sum() > 500 && inputs.All(c => narrow[c.Key] >= c.Minimum),
            "Narrow viewport must retain readable minima and scrolling.");
        var manual = inputs.Select(c => c.Key == "m_ucost" ? c with { Preferred = 140 } : c).ToArray();
        var wide = SummaryColumnFit.Calculate(manual, 1800, 1);
        Require(wide["m_ucost"] == 140 && manual.Single(c => c.Key == "m_ucost").Preferred == 140,
            "User width preference was not preserved.");

        var manualNarrow = EstimateColumnLayout.Fit(manual, 500, manual: true);
        var manualWide = EstimateColumnLayout.Fit(manual, 2400, manual: true);
        Require(manual.All(c => manualNarrow[c.Key] == c.Preferred && manualWide[c.Key] == c.Preferred),
            "Manual summary widths must survive viewport changes without moving neighbours.");

        var grid = new DataGrid { FontSize = 13, AutoGenerateColumns = false };
        var money = new DataGridTextColumn { SortMemberPath = "t_cost", Header = new TextBlock { Text = "Закупка ∑" } };
        grid.Columns.Add(money);
        grid.ItemsSource = new[] { new EstimateView.SummaryRow { TotalCostText = "1,00" } };
        var cache = new SummaryColumnFit(grid, column => 80);
        var first = cache.GetMinimums(1, 1);
        var small = first[money];
        Require(ReferenceEquals(first, cache.GetMinimums(1, 1)), "Unchanged source must reuse measurements.");
        grid.ItemsSource = new[] { new EstimateView.SummaryRow { Kind = "total", TotalCostText = "-19 144 180 999,98" } };
        var second = cache.GetMinimums(1, 1);
        var large = second[money];
        Require(!ReferenceEquals(first, second) && large > small,
            "Replacement source must recalculate widths for large signed totals.");
        grid.ItemsSource = null;
        var empty = cache.GetMinimums(1, 1);
        Require(!ReferenceEquals(second, empty) && empty[money] <= large &&
            ReferenceEquals(empty, cache.GetMinimums(1, 1)), "Clearing the source must measure headers only.");
        Require(!ReferenceEquals(empty, cache.GetMinimums(1.25, 1.25)), "DPI change must invalidate measurements.");
        var beforeFont = cache.GetMinimums(1.25, 1.25);
        grid.FontSize = 16;
        Require(!ReferenceEquals(beforeFont, cache.GetMinimums(1.25, 1.25)), "Font change must invalidate measurements.");
        // A bounded text cache must not lose the maximum even after saturation,
        // nor change the result when identical displayed strings repeat.
        var values = Enumerable.Range(0, 25000).Select(i =>
            new EstimateView.SummaryRow { TotalCostText = i < 5000
                ? i.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
                : i == 24999 ? "-123 456 789 012 345,67" : "1,00" }).ToArray();
        grid.ItemsSource = values;
        var all = SummaryColumnFit.ReadMinimums(grid, 1)[money];
        grid.ItemsSource = new[] { values[^1] };
        var maximumOnly = SummaryColumnFit.ReadMinimums(grid, 1)[money];
        Require(all == maximumOnly, "Bounded measurement cache lost a late large value.");
        Console.WriteLine("Summary fit arithmetic and formatted-value checks passed (no SQL).");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
