using System.IO;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;
using GmcSmeta.App;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using PdfSharp.Pdf.IO;

namespace GmcSmeta.ContractExport.Checks;

internal static class Program
{
    private static int Main()
    {
        try
        {
            CheckKpForms();
            var (goldenEstimate, goldenHeader) = Fixture(12);
            GoldenChecks.Run(goldenEstimate, goldenHeader);
            CheckShipmentForms();
            CheckLargeEstimate(2500);
            CheckLargeEstimate(10000);
            CheckSqlPilotIfConfigured();
            Console.WriteLine("PASS synthetic contract-export checks: XLSX/PDF structure, cent-level formulas, shipments, 2500/10000 rows. Customer goldens and two-client SQL acceptance remain unverified.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void CheckKpForms()
    {
        var (estimate, header) = Fixture(12);
        var smr = new Dictionary<int, decimal> { [101] = 20m, [102] = 45m };

        using var regular = KpExcelForms.Create(estimate, header);
        var sheet = regular.Worksheet("КП");
        Assert(sheet.Column(2).Width == 52, "Python regular KP column widths");
        Assert(sheet.CellsUsed().Any(c => c.Value.ToString() == "Радиатор"),
            "visible parent position lost");
        Assert(sheet.CellsUsed().Any(c => c.Value.ToString() == "Комплектующее"),
            "unhidden child position lost");
        Assert(!sheet.CellsUsed().Any(c => c.Value.ToString() == "Скрыто вне КП"),
            "out_of_kp leaked into printed document");
        var priced = sheet.CellsUsed().First(c => c.Value.ToString() == "Радиатор").Address.RowNumber;
        Assert(sheet.Cell(priced, 6).FormulaA1 == "E" + priced + "*D" + priced,
            "regular line amount must be an Excel formula");
        Assert(sheet.Cell(priced, 5).GetDouble() == 120d, "retail rate");
        Assert(sheet.CellsUsed().Any(c => c.Value.ToString().Contains("Длинное название")),
            "200+ character product name missing");
        var longRow = sheet.CellsUsed().First(c =>
            c.Value.ToString().StartsWith("Длинное название", StringComparison.Ordinal))
            .Address.RowNumber;
        Assert(sheet.Cell(longRow, 2).Style.Alignment.WrapText &&
            sheet.Row(longRow).Height >= 60, "long product name is clipped");
        Assert(sheet.CellsUsed().Any(c => c.Value.ToString().Contains("ИТОГО ЗА МАТЕРИАЛ")),
            "material total");
        RoundTrip(regular, "kp-regular.xlsx");

        using var internalBook = KpExcelForms.Create(estimate, header, internalPrices: true);
        var internalSheet = internalBook.Worksheet("КП");
        Assert(internalSheet.Column(9).Width == 15, "Python internal KP width");
        var r = internalSheet.CellsUsed().First(c => c.Value.ToString() == "Радиатор").Address.RowNumber;
        Assert(internalSheet.Cell(r, 5).GetDouble() == 100d, "purchase cost must remain separate");
        Assert(internalSheet.Cell(r, 7).GetDouble() == 120d, "retail rate must remain separate");
        Assert(internalSheet.Cell(r, 9).FormulaA1 == "H" + r + "-F" + r,
            "margin formula");
        RoundTrip(internalBook, "kp-internal.xlsx");

        using var positional = KpExcelForms.Create(estimate, header, positional: true);
        var posSheet = positional.Worksheet("Материалы и работы");
        r = posSheet.CellsUsed().First(c => c.Value.ToString() == "Радиатор").Address.RowNumber;
        Assert(posSheet.Cell(r, 7).GetDouble() == 10d, "work price");
        Assert(posSheet.Cell(r, 8).FormulaA1 == "G" + r + "*D" + r, "work formula");
        RoundTrip(positional, "kp-positional.xlsx");

        var vatTop = estimate with
        {
            VatTop = true,
            Totals = estimate.Totals with { Net = 1505m, Vat = 301m, Total = 1806m }
        };
        using var vatBook = KpExcelForms.Create(vatTop, header, vatLine: true);
        Assert(vatBook.Worksheet("КП").CellsUsed().Any(c => c.Value.ToString() == "ИТОГО с НДС:"),
            "VAT-on-top footer");

        var fixedMode = estimate with { SmrMode = "amount" };
        using var fixedAmount = KpExcelForms.Create(fixedMode, header);
        var fixedRow = fixedAmount.Worksheet("КП").CellsUsed()
            .First(c => c.Value.ToString() == "СМР:").Address.RowNumber;
        Assert(fixedAmount.Worksheet("КП").Cell(fixedRow, 6).GetDouble() == 65d,
            "fixed SMR cost must preserve SQL model total");
        var systems = estimate with { SmrMode = "systems" };
        using var bySystem = KpExcelForms.Create(systems, header, smrBySystem: smr);
        Assert(bySystem.Worksheet("КП").CellsUsed().Any(c => c.Value.ToString() == "МАТЕРИАЛ:"),
            "per-system material subtotal");

        var percentMode = estimate with { SmrMode = "percent" };
        using var percent = KpExcelForms.Create(percentMode, header, smrPercent: 7.5m);
        Assert(percent.Worksheet("КП").CellsUsed().Any(c =>
            c.FormulaA1.Contains("*7.5/100", StringComparison.Ordinal)),
            "percentage SMR formula must preserve the contract percentage");

        var extended = new SqlServerKpRequisites.Values(
            new[] { "ООО ГМС", "г. Владивосток", "ИНН 123456" }, null,
            "ООО ГМС", "Генеральный директор", "И.И. Иванов",
            "АО Заказчик", "Директор", "П.П. Петров", "77", "21.11.2026");
        var dop = header with { Kind = "dop", Number = "88" };
        using var dopBook = KpExcelForms.Create(estimate, dop with { Id = estimate.ContractId },
            requisites: extended);
        Assert(dopBook.Worksheet(1).CellsUsed().Any(c =>
            c.Value.ToString().Contains("к Договору подряда № 77")),
            "DS must reference the parent contract");
        var pdf = KpPdfForms.Create(estimate, header);
        ValidatePdf(pdf, 1, "kp-regular.pdf");
        ValidatePdf(KpPdfForms.Create(estimate, header, positional: true), 1, "kp-positional.pdf");
        ValidatePdf(KpPdfForms.Create(estimate, header, internalPrices: true), 1, "kp-internal.pdf");
    }

    private static void CheckShipmentForms()
    {
        var header = new SqlServerShipmentPrintData.Snapshot(
            "7", new DateTime(2026, 11, 21), "Промышленный объект",
            "Мастер участка", "Прораб", "Выдано согласно заявке",
            true, new[]
            {
                new SqlServerShipmentPrintData.Line("Договор № 3", "В1", "Воздуховод", "м²", 15.25m, "", false),
                new SqlServerShipmentPrintData.Line("Договор № 3", "В1", "Решётка", "шт", 3m, "", false),
                new SqlServerShipmentPrintData.Line("Договор № 3", "В2", "Отвод", "шт", 10m, "", true)
            });
        using var book = ShipmentExcelForms.Create(header);
        var sheet = book.Worksheet("Ведомость");
        Assert(sheet.Column(2).Width == 62, "Python shipment name column width");
        Assert(sheet.Cell("A1").GetText().Contains("№ 7"), "shipment number");
        Assert(sheet.Cell("D6").GetDouble() == 15.25d, "issued quantity");
        Assert(sheet.Cell("E6").IsEmpty(), "the received checkbox must remain empty");
        Assert(sheet.Cell("F4").GetText() == "Примечание", "six-column shipment layout");
        RoundTrip(book, "shipment.xlsx");
        ValidatePdf(ShipmentPdfForms.Create(header), 1, "shipment.pdf");
        GoldenChecks.RunShipment(header);

        var many = header with
        {
            Lines = Enumerable.Range(0, 380).Select(i =>
                new SqlServerShipmentPrintData.Line("Договор № 3", "В1",
                    new string('Д', 140) + " " + i, "шт", 1m, "", false)).ToArray()
        };
        ValidatePdf(ShipmentPdfForms.Create(many), 3, "shipment-multipage.pdf");
    }

    private static void CheckSqlPilotIfConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("GMC_CONTRACT_EXPORT_SQL_PILOT");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine("SKIP SQL Server pilot: GMC_CONTRACT_EXPORT_SQL_PILOT not configured");
            return;
        }
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!builder.InitialCatalog.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQL smoke checks are pilot-only: GmcSmetaPilot_*");
        using var connection = SqlServerDatabase.Open(connectionString);
        var originalRevision = SqlServerDatabase.Revision(connection);
        using var select = new SqlCommand(
            "SELECT TOP(1) id FROM dbo.contracts ORDER BY id", connection);
        var contract = select.ExecuteScalar();
        if (contract is not null && contract is not DBNull)
        {
            var id = Convert.ToInt32(contract);
            var estimate = EstimateReader.Load(connection, id);
            var document = Workspace.Documents(connection).Single(d => d.Id == id);
            var requisites = SqlServerKpRequisites.Read(connection, id);
            var percent = (decimal)SqlServerWorkspace.ReadContract(connection, id).SmrPercent;
            var perSystem = new Dictionary<int, decimal>();
            using (var command = new SqlCommand(
                "SELECT id,smr_amount FROM dbo.systems WHERE contract_id=@id", connection))
            {
                command.Parameters.AddWithValue("@id", id);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    perSystem[Convert.ToInt32(reader.GetValue(0))] =
                        reader.IsDBNull(1) ? 0m : Convert.ToDecimal(reader.GetValue(1));
            }
            using var book = KpExcelForms.Create(estimate, document,
                smrBySystem: perSystem, requisites: requisites, smrPercent: percent);
            RoundTrip(book, "pilot-kp.xlsx");
            ValidatePdf(KpPdfForms.Create(estimate, document, requisites: requisites),
                1, "pilot-kp.pdf");
        }
        using var shipmentQuery = new SqlCommand(
            "SELECT TOP(1) object_id,id FROM dbo.shipments ORDER BY id", connection);
        using (var reader = shipmentQuery.ExecuteReader())
        {
            if (reader.Read())
            {
                var objectId = Convert.ToInt64(reader.GetValue(0));
                var shipmentId = Convert.ToInt64(reader.GetValue(1));
                reader.Close();
                var shipment = SqlServerShipmentPrintData.Read(connection, objectId, shipmentId);
                using var book = ShipmentExcelForms.Create(shipment);
                RoundTrip(book, "pilot-shipment.xlsx");
            }
        }
        Assert(originalRevision == SqlServerDatabase.Revision(connection),
            "Read-only export modified SQL Server revision");
        Console.WriteLine("PASS pilot SQL Server: read-only KP/shipment");
    }

    private static void CheckLargeEstimate(int count)
    {
        var (sample, header) = Fixture(count);
        var rows = new List<EstimateRow>
        {
            new(RowKind.System, 0, 0, 0, false, "1", "В1", "", 0m, 0m,
                0m, null, false, "") { SystemId = 101 }
        };
        for (var i = 0; i < count; i++)
            rows.Add(new EstimateRow(RowKind.Position, 1, i + 1, 1000 + i,
                false, (i + 1).ToString(), "Воздуховод тип " + i,
                "м", 2m, 50m, 100m, 45m, false, "")
            { SystemId = 101, WorkPrice = 5m });
        var estimate = sample with
        {
            Positions = count,
            Rows = rows,
            PositionsInOrder = rows.Where(x => !x.IsSystem).ToArray(),
            Totals = sample.Totals with { Materials = count * 100m,
                Smr = count * 10m, Net = count * 110m,
                Total = count * 110m, Positions = count }
        };
        using var workbook = KpExcelForms.Create(estimate, header);
        var worksheet = workbook.Worksheet(1);
        Assert(worksheet.CellsUsed().Count() > count * 5, "large KP rows omitted");
        RoundTrip(workbook, count + "-rows.xlsx");
        if (count == 2500)
        {
            var limitedRows = rows.Take(420).ToArray();
            var limited = estimate with
            {
                Rows = limitedRows,
                Positions = limitedRows.Length - 1,
                Totals = estimate.Totals with
                {
                    Materials = (limitedRows.Length - 1) * 100m,
                    Smr = (limitedRows.Length - 1) * 10m,
                    Net = (limitedRows.Length - 1) * 110m,
                    Total = (limitedRows.Length - 1) * 110m
                }
            };
            ValidatePdf(KpPdfForms.Create(limited, header), 3, "kp-multipage.pdf");
        }
    }

    private static (EstimateDocument, DocRow) Fixture(int count)
    {
        var positions = new[]
        {
            new EstimateRow(RowKind.Position, 1, 1, 10, false, "2", "Радиатор",
                "шт", 2m, 120m, 240m, 100m, false, "") { SystemId = 101, WorkPrice = 10m },
            new EstimateRow(RowKind.Position, 2, 2, 11, true, "", "Комплектующее",
                "шт", 1m, 0m, 0m, null, false, "") { SystemId = 101, ParentPositionId = 10 },
            new EstimateRow(RowKind.Position, 1, 3, 12, false, "3", "Скрыто вне КП",
                "шт", 1m, 999m, 999m, 800m, true, "") { SystemId = 101 },
            new EstimateRow(RowKind.Position, 1, 5, 20, false, "5", "Длинное название " +
                new string('Д', 200), "шт", 3m, 400m, 1200m, 350m, false, "")
                { SystemId = 102, WorkPrice = 15m }
        };
        var rows = new List<EstimateRow>
        {
            new(RowKind.System, 0, 0, 0, false, "1", "В1", "", 0m, 0m, 240m, null, false, "")
                { SystemId = 101 }
        };
        rows.AddRange(positions.Take(3));
        rows.Add(new EstimateRow(RowKind.System, 0, 4, 0, false, "4", "В2", "", 0m,
            0m, 1200m, null, false, "") { SystemId = 102 });
        rows.Add(positions[3]);
        var totals = new ContractTotals(1, "main", "std", 20m,
            1440m, 65m, 1505m, 1505m, 250.83m, count);
        var document = new EstimateDocument(1, "Договор №42", "Договор",
            "Цех сборки", "ООО ГМС", "std", 20m, false, false, "",
            2, count, rows, positions, totals) { SmrMode = "pos" };
        var header = new DocRow(1, 100, "main", "Договор", "Договор №42",
            "", "42", "01.11.2026", false, 1440m, 65m, 1505m, count);
        return (document, header);
    }

    private static void RoundTrip(XLWorkbook workbook, string filename)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        Assert(stream.Length > 800, "XLSX missing: " + filename);
        var folder = Environment.GetEnvironmentVariable("GMC_CONTRACT_EXPORT_SAMPLES");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, filename), stream.ToArray());
        }
        stream.Position = 0;
        using var check = new XLWorkbook(stream);
        Assert(check.Worksheets.Count == 1, "XLSX roundtrip: " + filename);
    }

    private static void ValidatePdf(byte[] contents, int minPages, string filename)
    {
        Assert(contents.Length > 400 && contents[0] == '%' && contents[1] == 'P',
            "PDF header: " + filename);
        using var stream = new MemoryStream(contents);
        using var pdf = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        Assert(pdf.PageCount >= minPages, "PDF page breaks missing: " + filename);
        var folder = Environment.GetEnvironmentVariable("GMC_CONTRACT_EXPORT_SAMPLES");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, filename), contents);
        }
    }

    private static void Assert(bool check, string error)
    {
        if (!check) throw new InvalidDataException(error);
    }
}
