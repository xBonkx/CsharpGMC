using System.Diagnostics;
using System.IO;
using ClosedXML.Excel;
using PdfSharp.Pdf.IO;
using GmcSmeta.App;
using GmcSmeta.Core.Db;

internal static class ProcurementExportChecks
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException("PROCUREMENT EXPORT: " + message);
        checks++;
    }

    public static void Run(string? target)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(target)) Directory.CreateDirectory(target);
            var doc = new ProcurementRequestPlanner.DocumentInfo(
                "Объект Вентиляция А",
                "г. Владивосток, ул. Тестовая, 7",
                "Договор №18",
                "ООО «ГМС»\nИНН 2537101261");
            foreach (var count in new[] { 2, 80, 2500 })
            {
                var watch = Stopwatch.StartNew();
                var rows = Enumerable.Range(1, count).Select(i =>
                    Buy(i, i % 2 == 0 ? "in" : "issued", $"Воздуховод металлический {i} " +
                        new string('А', i % 10 == 0 ? 130 : 5), i % 4 == 0 ? "В-1" : "П-1")).ToArray();
                var issues = rows.Take(30).ToDictionary(x => x.PositionId,
                    x => "1,5 (21.11.26), 2 (22.11.26)");
                var snapshot = new ProcurementControlExport.Snapshot(doc, rows, issues);
                using (var workbook = ProcurementControlExport.CreateWorkbook(snapshot, withMoney: true))
                {
                    var sheet = workbook.Worksheet("Контроль");
                    Check(sheet.Cell(1, 1).GetString().Contains("Объект Вентиляция А"),
                        "Control document name");
                    Check(workbook.Worksheets.Count == 2, "Status legend worksheet");
                    Check(sheet.CellsUsed().Any(x => x.GetString().Contains("Выдачи")),
                        "History column");
                    Check(sheet.CellsUsed().Any(x => x.FormulaA1.Contains("*")),
                        "Margin Excel formula absent");
                    Roundtrip(workbook, PathFor(target, $"control-{count}.xlsx", count));
                }
                if (count != 2500)
                {
                    var pdf = ProcurementPdfExport.CreateControl(snapshot, withMoney: true);
                    ValidatePdf(pdf, count == 80 ? 2 : 1, PathFor(target, $"control-{count}.pdf", count));
                }
                var request = rows.Select(x => new ProcurementRequestPlanner.RequestRow
                {
                    Name = x.Name, Unit = x.Unit, Quantity = x.Qty,
                    UnitPrice = x.CostPrice, Note = x.Note
                }).ToArray();
                using (var workbook = ProcurementRequestExcel.Create(doc, "Поставщик",
                    "154", new DateTime(2026, 11, 21), "28.11.2026",
                    "Проверить комплектность", "Инженер ПТО", request, withPrices: true))
                {
                    var sheet = workbook.Worksheet("Заявка");
                    Check(sheet.CellsUsed().Any(x => x.GetString().Contains("ЗАЯВКА № 154")),
                        "Request number");
                    Check(sheet.CellsUsed().Any(x => x.FormulaA1.StartsWith("SUM(")),
                        "Request total formula");
                    Check(sheet.CellsUsed().Any(x => x.FormulaA1.Contains("*")),
                        "Request line multiplication");
                    Roundtrip(workbook, PathFor(target, $"request-{count}.xlsx", count));
                }
                if (count != 2500)
                {
                    var shop = rows.Select(x => x with
                    {
                        Source = "shop", Status = "shop_queued",
                        StatusLabel = "Заявка в цех"
                    }).ToArray();
                    var pdf = ProcurementPdfExport.CreateShopOrder(doc, shop, "Ц-7", "28.11.2026");
                    ValidatePdf(pdf, count == 80 ? 2 : 1,
                        PathFor(target, $"shop-order-{count}.pdf", count));
                    var preview = ProcurementRequestPlanner.ShopPrintRows(shop)
                        .Select(line => line with { Note = "Правка только для печати", Quantity = 1.25m }).ToArray();
                    ValidatePdf(ProcurementPdfExport.CreateShopOrder(doc, preview, "Ц-8", "Срочно"),
                        count == 80 ? 2 : 1, PathFor(target, $"shop-preview-{count}.pdf", count));
                    Check(shop.All(x => x.Qty == 3m), "Print preview changed source quantity");
                }
                watch.Stop();
                Console.WriteLine($"PASS {count} rows: control XLSX/PDF, request XLSX, shop PDF; " +
                    $"elapsed {watch.ElapsedMilliseconds} ms");
            }
            Console.WriteLine($"PROCUREMENT OFFLINE EXPORT CHECKS PASSED {checks}/{checks}");
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("PROCUREMENT CHECKS FAILED", ex);
        }
    }

    private static SqlServerProcurement.BuyRow Buy(int i, string kind, string name, string system) =>
        new(i, system, name, "шт", 3m, 50m, kind,
            SqlServerProcurement.StatusLabel(kind), "#EFF6FF", "#1D4ED8",
            "Поставщик", kind, "Заявка №154", "№ счет 18", 40m,
            new DateTime(2026, 11, 28), i % 2 == 0 ? "Замена" : "", "Тест",
            false, 2m, 1m, false, false, Array.Empty<string>(),
            Source: "buy", SystemId: i % 4 == 0 ? 2 : 1);

    private static string? PathFor(string? directory, string filename, int count) =>
        string.IsNullOrWhiteSpace(directory) || count > 80
            ? null : Path.Combine(directory, filename);

    private static void Roundtrip(XLWorkbook source, string? target)
    {
        using var ms = new MemoryStream();
        source.SaveAs(ms);
        Check(ms.Length > 2000, "XLSX output too small");
        if (target is not null) File.WriteAllBytes(target, ms.ToArray());
        ms.Position = 0;
        using var reopened = new XLWorkbook(ms);
        Check(reopened.Worksheets.Count == source.Worksheets.Count, "Sheet count changed");
        foreach (var worksheet in source.Worksheets)
        {
            var restored = reopened.Worksheet(worksheet.Name);
            Check(restored.RangeUsed() is not null, "XLSX sheet empty: " + worksheet.Name);
            foreach (var cell in worksheet.CellsUsed(XLCellsUsedOptions.Contents))
            {
                var read = restored.Cell(cell.Address);
                var position = worksheet.Name + "!" + cell.Address;
                if (cell.HasFormula)
                    Check(cell.FormulaA1 == read.FormulaA1,
                        "Formula changed after roundtrip: " + position);
                else if (cell.DataType == XLDataType.Number)
                    Check(cell.GetValue<decimal>() == read.GetValue<decimal>(),
                        "Numeric quantity or price changed after roundtrip: " + position);
                else if (cell.DataType == XLDataType.DateTime)
                    Check(cell.GetDateTime() == read.GetDateTime(),
                        "Date changed after roundtrip: " + position);
                else
                    Check(cell.GetString() == read.GetString(),
                        "Text changed after roundtrip: " + position);
            }
        }
    }

    private static void ValidatePdf(byte[] data, int minPages, string? target)
    {
        Check(data.Length > 1500, "PDF too small");
        Check(System.Text.Encoding.ASCII.GetString(data, 0, 5) == "%PDF-", "PDF signature");
        using var stream = new MemoryStream(data);
        using var pdf = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        Check(pdf.PageCount >= minPages, $"PDF page count too small: {pdf.PageCount}");
        if (target is not null) File.WriteAllBytes(target, data);
    }
}
