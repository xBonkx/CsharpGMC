using ClosedXML.Excel;
using GmcSmeta.App;
using Row = GmcSmeta.App.EstimateView.SummaryRow;

internal static class SummaryExcelChecks
{
    internal static void Run()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Сводная");
        SummaryDisplayedExcel.Fill(sheet, new[]
        {
            new Row { Kind = "system", Name = "Система", QtyText = "2 поз." },
            new Row { Name = "=1+2", Unit = "шт", QtyText = "1,25",
                CostUnitText = "1\u00a0234,50", PriceUnitText = "—",
                MCostText = "1\u202f543,12", WorkSellText = "0,00",
                MatMarginText = "−15,00", MarkupText = "50,00" },
            new Row { Name = "Вторая", QtyText = "2", WorkCostText = "" },
            new Row { Kind = "total", Name = "ИТОГО", TotalCostText = "1 543,12" },
        });
        using var stream = new System.IO.MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        using var reopened = new XLWorkbook(stream);
        var saved = reopened.Worksheet(1);
        Require(saved.Cell("A3").DataType == XLDataType.Text && !saved.Cell("A3").HasFormula &&
            saved.Cell("A3").GetString() == "=1+2", "Names must remain literal.");
        Require(saved.Cell("C2").GetString() == "2 поз.", "Group label lost.");
        Require(saved.Cell("C3").DataType == XLDataType.Number && saved.Cell("C3").GetDouble() == 1.25, "Quantity not numeric.");
        Require(saved.Cell("C4").Style.NumberFormat.Format == "#,##0", "Whole quantity separator.");
        Require(saved.Cell("D3").DataType == XLDataType.Number && saved.Cell("D3").GetDouble() == 1234.5, "Money not numeric.");
        Require(saved.Cell("E3").GetString() == "—", "Missing marker lost.");
        Require(saved.Cell("F3").GetDouble() == 1543.12, "Narrow space parsing.");
        Require(saved.Cell("I3").DataType == XLDataType.Number && saved.Cell("I3").GetDouble() == 0, "Zero lost.");
        Require(saved.Cell("H4").IsEmpty(), "Missing value must remain blank.");
        Require(saved.Cell("N3").GetDouble() == -15 && saved.Cell("P3").GetDouble() == 50, "Margin/markup.");
        Require(saved.Cell("L5").GetDouble() == 1543.12 && saved.Cell("A5").Style.Font.Bold, "Total or style lost.");
        using (var created = SummaryDisplayedExcel.Create(new[] { new Row { Name = "Контроль", QtyText = "1,25" } },
                   CancellationToken.None))
        {
            Require(created.Worksheet(1).Name == "Лист1" && created.Worksheet(1).Cell("C2").GetDouble() == 1.25,
                "Background workbook preserves sheet and quantity.");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                using var unused = SummaryDisplayedExcel.Create(Array.Empty<Row>(), cancelled.Token);
                throw new Exception("Cancelled summary export must not create workbook.");
            }
            catch (OperationCanceledException) { }
        }
        Console.WriteLine("Summary displayed XLSX numeric cells and missing markers OK.");
        CheckKsProgress();
    }

    private static void CheckKsProgress()
    {
        var acts = new[] {
            new GmcSmeta.Core.Db.SqlServerActs.ActInfo(101, "1", new DateTime(2026, 11, 1),
                null, null, true, 1, 50m),
            new GmcSmeta.Core.Db.SqlServerActs.ActInfo(102, "2", new DateTime(2026, 11, 2),
                null, null, false, 1, 20m)
        };
        var systems = new List<(GmcSmeta.Core.Db.SqlServerActs.SummarySystem System, bool SectionHeader)>
            { (new(1, "П1", false), false), (new(2, "Пустая", false), true) };
        var positions = new[] {
            new GmcSmeta.Core.Db.SqlServerActs.SummaryRow(1, 1, "1", "Позиция", "шт",
                5m, 10m, 5m, 50m, 0m, 0m, 5m,
                new Dictionary<long, decimal> { [101] = 5m }),
            new GmcSmeta.Core.Db.SqlServerActs.SummaryRow(2, 1, "2", "Остаток", "шт",
                4m, 10m, 1m, 10m, 3m, 30m, 4m,
                new Dictionary<long, decimal> { [102] = 1m })
        };
        var summary = new GmcSmeta.Core.Db.SqlServerActs.SummaryData(acts, systems, positions);
        var totals = new GmcSmeta.Core.Domain.ContractTotals(1, "main", "", 0m,
            90m, 0m, 90m, 90m, 0m, 2);
        using var book = KsProgressWorkbook.Create(summary, totals, acts, "Объект", new DateTime(2026,11,28));
        using var bytes = new System.IO.MemoryStream();
        book.SaveAs(bytes);
        bytes.Position = 0;
        using var reopened = new XLWorkbook(bytes);
        var sheet = reopened.Worksheet(1);
        Require(sheet.Cell("A1").GetString() == "Объект — сводная по выполнению", "Captured title.");
        Require(sheet.Cell("A2").GetString() == "на 28.11.2026", "Captured date.");
        Require(sheet.Cell("G7").GetDouble() == 5 && sheet.Cell("H7").IsEmpty(), "Sparse act columns.");
        Require(sheet.Cell("H8").GetDouble() == 1 && sheet.Cell("G8").IsEmpty(), "Second act.");
        Require(sheet.Cell("B9").GetString() == "Пустая", "Empty header retained.");
        Require(sheet.Cell("F10").GetDouble() == 90 && sheet.Cell("J10").GetDouble() == 60 &&
            sheet.Cell("L10").GetDouble() == 30, "Summary totals.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            using var cancelled = KsProgressWorkbook.Create(summary, totals, acts, "", DateTime.Today, cancellation.Token);
            throw new Exception("Cancelled export must not create workbook.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("KS progress workbook totals, sparse cells, captured date and cancellation OK.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
