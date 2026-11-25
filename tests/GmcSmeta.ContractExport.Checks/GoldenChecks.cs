using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ClosedXML.Excel;
using GmcSmeta.App;
using GmcSmeta.Core.Domain;
using PdfSharp.Pdf.IO;

namespace GmcSmeta.ContractExport.Checks;

/// <summary>
/// Independent, deterministic synthetic goldens. These are NOT customer-approved
/// templates and must never be reported as proof of visual Python/WPF parity.
/// All data are in memory; no SQL connection or write path exists here.
/// </summary>
internal static class GoldenChecks
{
    internal static void Run(EstimateDocument source, DocRow header)
    {
        var system = source.Rows.First(x => x.IsSystem);
        var item = source.Rows.First(x => !x.IsSystem && x.Name == "Радиатор")
            with { Qty = 3m, Price = 17.19m, PurchasePrice = 11.07m, WorkPrice = 4.13m };
        var estimate = source with
        {
            Rows = new[] { system, item },
            PositionsInOrder = new[] { item },
            Positions = 1,
            SmrMode = "amount",
            VatTop = true,
            Totals = source.Totals with
            {
                Materials = 51.57m, Smr = 0m, Net = 51.57m,
                Vat = 10.31m, Total = 61.88m, Positions = 1
            }
        };

        using (var book = KpExcelForms.Create(estimate, header))
        {
            var sheet = book.Worksheet("КП");
            var r = Line(sheet, "Радиатор");
            ExpectClose(sheet.Cell(r, 4).GetDouble(), 3d, "regular quantity");
            ExpectClose(sheet.Cell(r, 5).GetDouble(), 17.19d, "regular price");
            Expect(sheet.Cell(r, 6).FormulaA1 == "E" + r + "*D" + r,
                "regular row formula");
            ExpectClose(sheet.Cell(r, 6).GetDouble(), 51.57d, "regular material cents");
            var vat = Line(sheet, "НДС 20%:");
            Expect(sheet.Cell(vat, 6).FormulaA1.Contains("ROUND(", StringComparison.Ordinal),
                "VAT on top must use ROUND(...,2)");
            ExpectClose(sheet.Cell(vat, 6).GetDouble(), 10.31d, "VAT cents");
            ExpectClose(sheet.Cell(Line(sheet, "ИТОГО с НДС:"), 6).GetDouble(), 61.88d,
                "gross cents");
            CheckXlsx(book, "golden-regular-cents.xlsx", "portrait", 6);
        }

        using (var book = KpExcelForms.Create(estimate, header, internalPrices: true))
        {
            var sheet = book.Worksheet("КП");
            var r = Line(sheet, "Радиатор");
            ExpectClose(sheet.Cell(r, 5).GetDouble(), 11.07d, "purchase unit cents");
            ExpectClose(sheet.Cell(r, 7).GetDouble(), 17.19d, "retail unit cents");
            Expect(sheet.Cell(r, 6).FormulaA1 == "E" + r + "*D" + r,
                "purchase amount formula");
            Expect(sheet.Cell(r, 8).FormulaA1 == "G" + r + "*D" + r,
                "retail amount formula");
            Expect(sheet.Cell(r, 9).FormulaA1 == "H" + r + "-F" + r,
                "margin formula");
            ExpectClose(sheet.Cell(r, 6).GetDouble(), 33.21d, "purchase amount cents");
            ExpectClose(sheet.Cell(r, 8).GetDouble(), 51.57d, "retail amount cents");
            ExpectClose(sheet.Cell(r, 9).GetDouble(), 18.36d, "margin cents");
            CheckXlsx(book, "golden-internal-cents.xlsx", "landscape", 9);
        }

        var positional = estimate with
        {
            SmrMode = "pos",
            Totals = estimate.Totals with
            {
                Smr = 12.39m, Net = 63.96m, Vat = 12.79m, Total = 76.75m
            }
        };
        using (var book = KpExcelForms.Create(positional, header, positional: true))
        {
            var sheet = book.Worksheet("Материалы и работы");
            var r = Line(sheet, "Радиатор");
            Expect(sheet.Cell(r, 6).FormulaA1 == "D" + r + "*E" + r,
                "positional material formula");
            Expect(sheet.Cell(r, 8).FormulaA1 == "G" + r + "*D" + r,
                "positional work formula");
            ExpectClose(sheet.Cell(r, 6).GetDouble(), 51.57d, "positional material cents");
            ExpectClose(sheet.Cell(r, 8).GetDouble(), 12.39d, "positional work cents");
            ExpectClose(sheet.Cell(Line(sheet, "ИТОГО с НДС"), 6).GetDouble(), 76.75d,
                "positional VAT-on-top cents");
            CheckXlsx(book, "golden-positional-cents.xlsx", "landscape", 8);
        }

        VerifyPdf(KpPdfForms.Create(estimate, header), "golden-regular-cents.pdf",
            false, "Радиатор", "Цех сборки", "61,88");
        VerifyPdf(KpPdfForms.Create(estimate, header, internalPrices: true),
            "golden-internal-cents.pdf", true, "Радиатор", "11,07", "17,19", "18,36");
        VerifyPdf(KpPdfForms.Create(positional, header, positional: true),
            "golden-positional-cents.pdf", true, "Радиатор", "12,39", "76,75");

        // Python 0.67.1: discount is a separate formula, never a price mutation.
        var discounted = estimate with
        {
            VatTop = false,
            Totals = estimate.Totals with { Total = 48.99m, DiscountPercent = 5m, DiscountAmount = 2.58m, Vat = 8.165m }
        };
        foreach (var internalPrices in new[]{false,true})
        {
            using var book=KpExcelForms.Create(discounted,header,internalPrices:internalPrices,vatLine:true);
            var sheet=book.Worksheet("КП");
            var col=internalPrices?8:6;
            var disc=Line(sheet,"Скидка 5%:");
            var final=Line(sheet,"ИТОГО С УЧЁТОМ СКИДКИ:");
            ExpectClose(sheet.Cell(disc,col).GetDouble(),-2.58d,"discount cents");
            ExpectClose(sheet.Cell(final,col).GetDouble(),48.99d,"discounted gross cents");
            Expect(sheet.Cell(final,col).FormulaA1.Contains(XLHelper.GetColumnLetterFromNumber(col)+disc,StringComparison.Ordinal),"final references discount");
            ExpectClose(sheet.Cell(Line(sheet,"Радиатор"),internalPrices?7:5).GetDouble(),17.19d,"unchanged retail unit");
            var vat=Line(sheet,"в т.ч. НДС 20%:");
            Expect(sheet.Cell(vat,col).FormulaA1.Contains(XLHelper.GetColumnLetterFromNumber(col)+final,StringComparison.Ordinal),"VAT references discounted gross");
        }
        var discountedPositional=positional with
        {
            VatTop=false,
            Totals=positional.Totals with { Total=60.76m, DiscountPercent=5m,DiscountAmount=3.20m,Vat=10.126666666666666666666666667m }
        };
        using(var book=KpExcelForms.Create(discountedPositional,header,positional:true,vatLine:true))
        {
            var sheet=book.Worksheet("Материалы и работы");
            ExpectClose(sheet.Cell(Line(sheet,"Скидка 5%"),6).GetDouble(),-3.20d,"positional discount cents");
            ExpectClose(sheet.Cell(Line(sheet,"ИТОГО С УЧЁТОМ СКИДКИ"),6).GetDouble(),60.76d,"positional payable");
        }
        VerifyPdf(KpPdfForms.Create(discounted,header,vatLine:true),"golden-discount.pdf",false,"Скидка","48,99");

        Console.WriteLine("PASS synthetic golden: KP discounts and existing XLSX/PDF cases");
        Console.WriteLine("NOTE synthetic checks cannot establish approved customer forms or SQL snapshot isolation");
    }

    internal static void RunShipment(GmcSmeta.Core.Db.SqlServerShipmentPrintData.Snapshot shipment)
    {
        using var book = ShipmentExcelForms.Create(shipment);
        var sheet = book.Worksheet("Ведомость");
        Expect(sheet.Cell("D6").GetDouble() == 15.25d, "shipment issued quantity");
        Expect(sheet.Cell("E6").IsEmpty(), "shipment receipt mark must be blank");
        Expect(sheet.Cell("F4").GetString() == "Примечание", "six-column shipment form");
        Expect(sheet.CellsUsed().Any(c => c.GetString().Contains(shipment.IssuedBy,
            StringComparison.Ordinal)), "shipment issuer signature");
        Expect(sheet.CellsUsed().Any(c => c.GetString().Contains(shipment.Recipient,
            StringComparison.Ordinal)), "shipment recipient signature");
        CheckXlsx(book, "golden-shipment.xlsx", "portrait", 6);
        VerifyPdf(ShipmentPdfForms.Create(shipment), "golden-shipment.pdf", false,
            "Ведомость выдачи", "Воздуховод", "Мастер участка", "Прораб");
        Console.WriteLine("PASS synthetic shipment: XLSX OOXML, quantity, signatures; PDF opens");
    }

    private static int Line(IXLWorksheet sheet, string label)
    {
        var match = sheet.CellsUsed()
            .FirstOrDefault(c => c.GetString() == label);
        if (match is null) throw new InvalidDataException("Missing cell: " + label);
        return match.Address.RowNumber;
    }

    private static void CheckXlsx(XLWorkbook workbook, string name,
        string orientation, int lastColumn)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        var bytes = stream.ToArray();
        Expect(bytes.Length > 1000, name + " empty XLSX");
        using var reopened = new XLWorkbook(new MemoryStream(bytes));
        var sheet = reopened.Worksheet(1);
        Expect(sheet.Columns(1, lastColumn).Count() == lastColumn,
            name + " column count");
        Expect(sheet.Column(2).Width >= 50d, name + " name column width");
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var page = Xml(archive, "xl/worksheets/sheet1.xml");
        Expect(page.Descendants().Any(e => e.Name.LocalName == "pageSetup" &&
            (string?)e.Attribute("orientation") == orientation),
            name + " print orientation");
        Expect(page.Descendants().Any(e => e.Name.LocalName == "f"),
            name + " formulas lost in OOXML");
        var metadata = Xml(archive, "xl/workbook.xml");
        var names = metadata.Descendants().Where(e => e.Name.LocalName == "definedName")
            .Select(e => (string?)e.Attribute("name")).ToHashSet();
        Expect(names.Contains("_xlnm.Print_Area"), name + " print area missing");
        Expect(names.Contains("_xlnm.Print_Titles"), name + " repeated table header missing");
        SaveSample(name, bytes);
    }

    private static XDocument Xml(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidDataException("Missing OOXML part: " + entryName);
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static void VerifyPdf(byte[] bytes, string name, bool landscape,
        params string[] requiredText)
    {
        using (var stream = new MemoryStream(bytes))
        using (var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import))
        {
            Expect(doc.PageCount >= 1, name + " has no PDF pages");
            var first = doc.Pages[0];
            Expect(landscape ? first.Width.Point > first.Height.Point :
                first.Height.Point > first.Width.Point, name + " orientation mismatch");
        }
        SaveSample(name, bytes);

        // PDFsharp opens pages but does not extract printable text. Test that
        // separately with real Poppler pdftotext when installed; never fake PASS.
        var configured = Environment.GetEnvironmentVariable("GMC_CONTRACT_EXPORT_PDFTOTEXT");
        var exe = string.IsNullOrWhiteSpace(configured) ? "pdftotext" : configured;
        var temp = Path.Combine(Path.GetTempPath(),
            "gmc-contract-golden-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(temp, bytes);
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "-enc", "UTF-8", temp, "-" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("pdftotext did not start");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidDataException("pdftotext timeout: " + name);
            }
            Expect(process.ExitCode == 0,
                "pdftotext failed: " + name + " " + error);
            var compact = Compact(output);
            foreach (var expected in requiredText)
                Expect(compact.Contains(Compact(expected), StringComparison.Ordinal),
                    name + " PDF text missing: " + expected);
            if (name == "golden-internal-cents.pdf")
            {
                // Both the material row and the summary must carry each total.
                Expect(Occurrences(compact, "33,21") >= 2,
                    "internal PDF purchase subtotal missing");
                Expect(Occurrences(compact, "18,36") >= 2,
                    "internal PDF margin subtotal missing");
            }
            Console.WriteLine("PASS PDF text/cyrillic: " + name);
        }
        catch (Win32Exception) when (string.IsNullOrWhiteSpace(configured))
        {
            Console.WriteLine("SKIP PDF text extraction: pdftotext not installed; " + name);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static int Occurrences(string text, string term)
    {
        var result = 0;
        for (var index = 0; (index = text.IndexOf(term, index,
            StringComparison.Ordinal)) >= 0; index += term.Length) result++;
        return result;
    }

    private static string Compact(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var ch in text)
            if (!char.IsWhiteSpace(ch)) result.Append(ch);
        return result.ToString().Normalize(NormalizationForm.FormC);
    }

    private static void SaveSample(string name, byte[] bytes)
    {
        var folder = Environment.GetEnvironmentVariable("GMC_CONTRACT_EXPORT_SAMPLES");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name), bytes);
    }

    private static void ExpectClose(double actual, double expected, string name) =>
        Expect(Math.Abs(actual - expected) < 0.000001d,
            name + ": expected " + expected.ToString("0.00", CultureInfo.InvariantCulture)
            + ", got " + actual.ToString("0.000000", CultureInfo.InvariantCulture));

    private static void Expect(bool ok, string name)
    {
        if (!ok) throw new InvalidDataException(name);
    }
}
