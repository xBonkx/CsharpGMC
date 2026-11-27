using System.IO;
using ClosedXML.Excel;
using GmcSmeta.App;
using GmcSmeta.Core.Db;

internal static class KsRequisitesChecks
{
    internal static void Run(Func<string, string, int, SqlServerKsPrintData.Package> fixture)
    {
        foreach (var (form, name) in new[] { ("default", "Объект"), ("dns", "ДНС"), ("r4", "Р4") })
        {
            var p = fixture(form, name, 2);
            var ds = p.Scope.Documents[0] with { Id = 2, Kind = "dop", Number = "4", BaseContractId = 1 };
            var act = p.Report.Acts[0] with { ContractId = 2 };
            var contractors = p.Contractors.ToDictionary(x => x.Key, x => x.Value);
            contractors[2] = contractors[1] with { DocumentId = 2, ContractDate = "2026-08-11", LegalName = "Неверный подрядчик ДС" };
            p = p with { Scope = p.Scope with { SelectedDocumentId = 2, Documents = new[] { p.Scope.Documents[0], ds }, Acts = new[] { act } },
                Report = p.Report with { Acts = new[] { act } }, Contractors = contractors };
            using var book = KsCustomerForms.Create(p);
            var ks2 = book.Worksheets.First();
            var ks3 = book.Worksheets.Last();
            var baseNumber = form == "r4" ? "F12" : "F11";
            Require(ks2.Cell(baseNumber).GetString() == "42", "KS-2 lost base number: " + form);
            if (form == "default")
            {
                Require(ks2.Cell("F12").GetString() == "01.01.2026", "Standard KS-2 used DS date.");
                Require(ks3.Cell("G13").GetString() == "01.01.2026", "Standard KS-3 used DS date.");
            }
            else
            {
                var dateRow = form == "dns" ? 12 : 13;
                Require(ks2.Cell("F" + dateRow).GetString() == "01" && ks2.Cell("G" + dateRow).GetString() == "01" &&
                    ks2.Cell("H" + dateRow).GetString() == "2026", "Customer KS-2 base date parts incorrect: " + form);
                var summaryRow = form == "dns" ? 14 : 13;
                Require(ks3.Cell("G" + summaryRow).GetString() == "01" && ks3.Cell("I" + summaryRow).GetString() == "2026",
                    "Customer KS-3 base date parts incorrect: " + form);
            }
            Require(ks2.CellsUsed().Any(c => c.GetString().Contains("4")), "DS number absent: " + form);
            Require(!ks2.CellsUsed().Any(c => c.GetString().Contains("Неверный подрядчик ДС")), "DS contractor replaced main: " + form);
            if (form == "dns")
            {
                Require(ks2.Cell("F13").GetString() == "4" && ks2.Cell("F14").GetString() == "11" &&
                    ks2.Cell("G14").GetString() == "08", "DNS DS fields not separate from main date.");
                Require(ks3.Cell("G15").GetString() == "4" && ks3.Cell("G16").GetString() == "11", "DNS KS-3 lost DS.");
            }
            if (form == "r4")
                Require(!ks2.CellsUsed().Where(c => c.Address.ToString() != "A1").Concat(ks3.CellsUsed()).Any(c =>
                    c.GetString().Contains("Цурупий") || c.GetString().Contains("Фактория")), "R4 leaked template requisites.");
            var originalGross = p.Report.PeriodGross;
            var gross = ks3.Cell(form == "dns" ? "G32" : "G29").GetDouble();
            Require(Math.Abs(gross - (double)originalGross) < .011, "Requisites changed a financial total: " + form);
            using var data = new MemoryStream();
            book.SaveAs(data);
            data.Position = 0;
            using var reopened = new XLWorkbook(data);
            Require(reopened.Worksheets.First().Cell(baseNumber).GetString() == "42", "Saved XLSX lost legal number.");
            var pdf = KsPdfForms.Create(p);
            Require(pdf.Length > 1500, "DS PDF export failed: " + form);
            var output = Environment.GetEnvironmentVariable("GMC_EXPORT_SAMPLE_DIR");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                File.WriteAllBytes(Path.Combine(output, form + "-ds-requisites.pdf"), pdf);
                File.WriteAllBytes(Path.Combine(output, form + "-ds-requisites.xlsx"), data.ToArray());
            }

            var extra = act with { DsNo = "5", DsDate = "2026-09-09", DsExtra = "№6 от 10.10.2026\n№7 от 11.11.2026" };
            using var extraBook = KsCustomerForms.Create(p with { Report = p.Report with { Acts = new[] { extra } } });
            Require(extraBook.Worksheets.First().CellsUsed().Any(c => c.GetString().Contains("№6 от 10.10.2026")),
                "Additional DS lines lost: " + form);
            Console.WriteLine("PASS legal/DS requisites, extra lines and unchanged totals: " + form);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
