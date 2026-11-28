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
        var beforeFamily = cache.GetMinimums(1.25, 1.25);
        grid.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        Require(!ReferenceEquals(beforeFamily, cache.GetMinimums(1.25, 1.25)), "Font family change must invalidate measurements.");
        var beforeStyle = cache.GetMinimums(1.25, 1.25);
        grid.FontStyle = FontStyles.Italic;
        Require(!ReferenceEquals(beforeStyle, cache.GetMinimums(1.25, 1.25)), "Font style change must invalidate measurements.");
        var beforeStretch = cache.GetMinimums(1.25, 1.25);
        grid.FontStretch = FontStretches.Expanded;
        Require(!ReferenceEquals(beforeStretch, cache.GetMinimums(1.25, 1.25)), "Font stretch change must invalidate measurements.");
        var beforePixels = cache.GetMinimums(1.25, 1.25);
        Require(!ReferenceEquals(beforePixels, cache.GetMinimums(1.25, 1.5)), "Pixels-per-DIP change must invalidate measurements.");
        var previousHeader = cache.GetMinimums(1.25, 1.25);
        ((TextBlock)money.Header).Text = "Длинноенеразрывноеназваниеколонки";
        var changedHeader = cache.GetMinimums(1.25, 1.25);
        Require(!ReferenceEquals(previousHeader, changedHeader) && changedHeader[money] > previousHeader[money],
            "A changed header must expand the measured minimum.");
        var additional = new DataGridTextColumn { SortMemberPath = "t_sell", Header = "Длинноенеразрывноеназваниеколонки" };
        grid.Columns.Add(additional);
        var added = cache.GetMinimums(1.25, 1.25);
        Require(added.ContainsKey(additional) && added[additional] > 64, "Added column with string header must be measured.");
        grid.Columns.Remove(additional);
        Require(!cache.GetMinimums(1.25, 1.25).ContainsKey(additional), "Removed column must not stay in width cache.");
        money.Header = new TextBlock { Text = "Закупка ∑" };
        grid.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        grid.FontStyle = FontStyles.Normal;
        grid.FontStretch = FontStretches.Normal;
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
        // Compatibility cost tables use already formatted values, never SQL or
        // live editor text, and keep the original preferred width as a floor.
        var legacy = new DataGrid { FontSize = 13, AutoGenerateColumns = false };
        var legacyMoney = new DataGridTextColumn { Header = "Закуп", Width = 88 };
        var legacyQty = new DataGridTextColumn { Header = "Кол-во", Width = 66 };
        legacy.Columns.Add(legacyMoney);
        legacy.Columns.Add(legacyQty);
        var legacyColumns = new (DataGridColumn Column, string Path, string Header, double Preferred)[]
        {
            (legacyMoney, "MCost", "Закуп", 88), (legacyQty, "Qty", "Кол-во", 66)
        };
        legacy.ItemsSource = new[] { new CostSummaryPreviewRow { MCost = "1,00", Qty = "1" } };
        var shortLegacy = LegacyCostColumnFit.ReadWidths(legacy, legacyColumns);
        legacy.ItemsSource = new[] { new CostSummaryPreviewRow { MCost = "−123 456 789 012 345,67", Qty = "1" } };
        var wideLegacy = LegacyCostColumnFit.ReadWidths(legacy, legacyColumns);
        Require(wideLegacy[0] > shortLegacy[0] && wideLegacy[1] == shortLegacy[1] &&
            shortLegacy[0] >= 88 && shortLegacy[1] >= 66, "Large formatted cost must expand only its default column.");
        Require(LegacyCostColumnFit.Value(new CostSummaryPreviewRow { IsWork = true, OriginalWorkUnitPrice = "123,45", UPrice = "999" },
            "SummaryUnitPrice") == "123,45", "Legacy measurement must use the visible work-unit value.");
        Require(LegacyCostColumnFit.Value(new CostSummaryPane.CostTotalsPreviewRow { Margin = "−1 000,00" },
            "Margin") == "−1 000,00", "Totals measurement must preserve formatted minus and separators.");
        Require(LegacyCostColumnFit.Value(new CostSummaryPane.SmrExpensePreviewRow { Amount = "19 000,00" },
            "Amount") == "19 000,00", "SMR expense amount must use its displayed value.");
        var display = new GridDisplayWidthMeasure(legacy);
        Require(display.Cell("123.456", 22) == display.Cell("123.456") + 22,
            "Expandable row must reserve its actual 22-DIP affordance.");
        Require(display.Header("Наименование Длинноенеразрывноеслово") > display.Header("Ед."),
            "Header measurements must include longest unbreakable word.");
        var smallFontWidth = display.Cell("−123 456 789,00");
        legacy.FontSize = 22;
        Require(new GridDisplayWidthMeasure(legacy).Cell("−123 456 789,00") > smallFontWidth,
            "Content-aware widths must grow with table font.");

        Console.WriteLine("Summary fit arithmetic and formatted-value checks passed (no SQL).");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
