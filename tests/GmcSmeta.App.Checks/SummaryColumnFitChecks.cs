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

        var grid = new DataGrid { FontSize = 13, AutoGenerateColumns = false };
        var money = new DataGridTextColumn { SortMemberPath = "t_cost", Header = new TextBlock { Text = "Закупка ∑" } };
        grid.Columns.Add(money);
        grid.ItemsSource = new[] { new EstimateView.SummaryRow { TotalCostText = "1,00" } };
        var small = SummaryColumnFit.ReadMinimums(grid, 1)[money];
        grid.ItemsSource = new[] { new EstimateView.SummaryRow { Kind = "total", TotalCostText = "-19 144 180 999,98" } };
        var large = SummaryColumnFit.ReadMinimums(grid, 1)[money];
        Require(large > small, "Large signed totals did not increase the readable minimum.");
        Console.WriteLine("Summary fit arithmetic and formatted-value checks passed (no SQL).");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
