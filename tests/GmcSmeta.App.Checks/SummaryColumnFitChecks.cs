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

        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        Require(DocumentColumnFit.Display(123456789012345.67m, "N2", ru) ==
            123456789012345.67m.ToString("N2", ru), "Document numeric display must match binding StringFormat.");
        Require(DocumentColumnFit.Display(1234.125m, "{}{0:0.###}", ru) == 1234.125m.ToString("0.###", ru),
            "Escaped composite WPF format must be preserved.");
        Require(DocumentColumnFit.Display(new DateTime(2026, 11, 28), "dd.MM.yyyy", ru) == "28.11.2026",
            "Date display must match WPF binding.");
        Require(DocumentColumnFit.Display(null, "N2", ru) == "", "Null value must not become zero.");
        var doc = new DataGrid { FontSize = 13, AutoGenerateColumns = false };
        var amountColumn = new DataGridTextColumn { Header = "Сумма", Width = 90 };
        var statusColumn = new DataGridTemplateColumn { Header = "Статус", Width = 100 };
        doc.Columns.Add(amountColumn);
        doc.Columns.Add(statusColumn);
        DocumentColumnFit.Input[] documentColumns =
        [
            new(amountColumn, "Amount", "Сумма", 90, "N2", ru, 0),
            new(statusColumn, "Status", "Статус", 100, null, ru, 20)
        ];
        doc.ItemsSource = new[] { new WidthFixture(1m, "Готово") };
        var shortDocument = DocumentColumnFit.ReadWidths(doc, documentColumns);
        doc.ItemsSource = new[] { new WidthFixture(-123456789012345.67m, "Готово") };
        var wideDocument = DocumentColumnFit.ReadWidths(doc, documentColumns);
        Require(wideDocument[0] > shortDocument[0] && wideDocument[1] == shortDocument[1],
            "A large formatted value expands only the corresponding default column.");
        doc.ItemsSource = new[] { new WidthFixture(1m, "Ожидается частичная поставка") };
        Require(DocumentColumnFit.ReadWidths(doc, documentColumns)[1] > shortDocument[1],
            "Template label measurement must include badge text.");
        Require(amountColumn.Width.Value == 90 && statusColumn.Width.Value == 100,
            "Measurement must not mutate column widths before application.");

        var stageTable = new System.Data.DataTable();
        stageTable.Columns.Add("S0", typeof(string));
        stageTable.Rows.Add("1");
        doc.ItemsSource = stageTable.DefaultView;
        DocumentColumnFit.Input[] stageColumns = [new(amountColumn, "S0", "Этап", 90, null, ru, 0)];
        var smallStage = DocumentColumnFit.ReadWidths(doc, stageColumns)[0];
        stageTable.Rows.Add("123 456 789 012 345,678");
        Require(DocumentColumnFit.ReadWidths(doc, stageColumns)[0] > smallStage,
            "DataRowView dynamic stage column must read the actual schema field.");
        doc.ItemsSource = new[] { new ConverterFixture(1L, new Dictionary<long, decimal> { [17] = 1m }) };
        DocumentColumnFit.Input[] converted =
        [
            new(amountColumn, "Area", "Площадь", 90, null, ru, 0, new MetalAreaConverter()),
            new(statusColumn, "PerAct", "КС", 90, null, ru, 0, new KsActValueConverter(), 17L)
        ];
        var smallConverted = DocumentColumnFit.ReadWidths(doc, converted);
        doc.ItemsSource = new[] { new ConverterFixture(123456789012345678L,
            new Dictionary<long, decimal> { [17] = 123456789012345.678m }) };
        var largeConverted = DocumentColumnFit.ReadWidths(doc, converted);
        Require(largeConverted[0] > smallConverted[0] && largeConverted[1] > smallConverted[1],
            "Approved display converters must measure formatted metal and per-act quantities.");
        Require(DocumentColumnFit.DefaultDisplayPath("TotalBefore") && !DocumentColumnFit.DefaultDisplayPath("Name") &&
            !DocumentColumnFit.DefaultDisplayPath("Nested.Value"), "Common fit roles must exclude prose and nested paths.");

        Console.WriteLine("Summary fit arithmetic and formatted-value checks passed (no SQL).");
    }

    public sealed record WidthFixture(decimal Amount, string Status);
    public sealed record ConverterFixture(long Area, IReadOnlyDictionary<long, decimal> PerAct);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
