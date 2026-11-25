using System.Globalization;
using System.IO;
using System.Text.Json;
using ClosedXML.Excel;
using PdfSharp.Pdf.IO;
using GmcSmeta.App;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

internal static class Program
{
    // Entire suite is OFFLINE: no connection string, SQL reader, or write is used.
    // These are synthetic oracle checks, NOT evidence of Python runtime or SQL Pilot parity.
    private static int _passed;
    private static int _failed;
    private static int _skipped;

    [STAThread]
    private static int Main()
    {
        Check("money: round_rows vs rows_exact at half kopeck", CheckRounding);
        Check("money: split SMR across systems without residual loss", CheckSystemSmr);
        foreach (var count in new[] { 300, 2500, 10000 })
        {
            var frozenCount = count;
            Check("money: deterministic " + count + " rows", () => CheckLargeMoney(frozenCount));
        }

        foreach (var form in new[] { "standard", "dns", "r4" })
        {
            var frozenForm = form;
            Check(form + ": KS2/KS3 XLSX, VOR XLSX and PDF with 2 rows",
                () => CheckDocuments(frozenForm, 2, includePdf: true));
            Check(form + ": KS2/KS3 XLSX and VOR XLSX with 300 rows",
                () => CheckDocuments(frozenForm, 300, includePdf: false));
            Check(form + ": signed/unsigned multi-DS group and formulas",
                () => CheckMultiDs(frozenForm));
        }

        foreach (var form in new[] { "dns", "r4" })
        {
            var frozenForm = form;
            Check(form + ": frozen KS3 gross opening balance", () => CheckOpening(frozenForm));
        }

        Check("v31: KS2/KS3 discount and fixed total formulas", CheckAdjustments);
        Check("v31: KS2 section amounts, duplicate names and remainder", CheckSectionAmounts);
        Check("DNS: XLSX formulas preserve half-kopeck round_rows", CheckDnsRoundRows);
        Check("R4: positional SMR is printed as work rows", CheckR4PositionSmr);
        Check("Dneprovsky: external customer KS/PDF template must fail closed",
            CheckDneprovsky);
        var pythonOracle = Environment.GetEnvironmentVariable("GMC_KS_PY_ORACLE");
        if (string.IsNullOrWhiteSpace(pythonOracle))
        {
            _skipped++;
            Console.WriteLine("SKIP real Python Act oracle: GMC_KS_PY_ORACLE is not set");
        }
        else
            Check("real Python Act model monetary oracle", () => CheckPythonOracle(pythonOracle));
        Console.WriteLine("RESULT pass=" + _passed + " fail=" + _failed +
            " skip=" + _skipped + "; Windows Excel/print preview and SQL Pilot NOT RUN by this executable");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string label, Action action)
    {
        try
        {
            action();
            _passed++;
            Console.WriteLine("PASS " + label);
        }
        catch (Exception error)
        {
            _failed++;
            Console.Error.WriteLine("FAIL " + label + " :: " +
                error.GetType().Name + ": " + error.Message);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void MoneyEquals(decimal expected, decimal actual, string label)
    {
        Assert(Math.Abs(expected - actual) <= 0.01m,
            label + ": expected " + expected.ToString("0.00", CultureInfo.InvariantCulture) +
            ", actual " + actual.ToString("0.00", CultureInfo.InvariantCulture));
    }

    private static void CheckRounding()
    {
        var rows = new[] { (Quantity: 1m, Price: 0.335m),
                           (Quantity: 1m, Price: 0.335m) };
        MoneyEquals(0.68m, KsFinancialPolicy.Materials(rows, false, true),
            "round_rows half-up");
        MoneyEquals(0.67m, KsFinancialPolicy.Materials(rows, true, false),
            "rows_exact sum then round");
        var vatTop = KsFinancialPolicy.Calculate(1000m, 200m, 30m, 20m,
            20m, true, 5);
        MoneyEquals(1500.05m, vatTop.Gross, "VAT on top plus 5 kopecks");
        MoneyEquals(1250.05m, vatTop.Ks3Net, "KS3 net with adjustment");
        var included = KsFinancialPolicy.Calculate(1000m, 200m, 30m, 20m,
            20m, false);
        MoneyEquals(1250m, included.Gross, "VAT included gross");
        MoneyEquals(208.33m, included.Vat, "VAT extracted from gross");
    }

    private static void CheckSystemSmr()
    {
        var lines = new[]
        {
            new KsActAllocation.Line(1, 1m, 1m, 0m),
            new KsActAllocation.Line(2, 1m, 1m, 0m),
            new KsActAllocation.Line(3, 1m, 1m, 0m)
        };
        var allocation = KsActAllocation.Split(lines, 100m, false);
        MoneyEquals(33.33m, allocation[1], "system 1");
        MoneyEquals(33.33m, allocation[2], "system 2");
        MoneyEquals(33.34m, allocation[3], "last system residual");
        MoneyEquals(100m, allocation.Values.Sum(), "allocated total");
    }

    private static void CheckLargeMoney(int count)
    {
        var rows = Enumerable.Range(0, count)
            .Select(_ => (Quantity: 2m, Price: 4.25m)).ToArray();
        var material = KsFinancialPolicy.Materials(rows, false, true);
        MoneyEquals(8.5m * count, material, "material " + count);
        var calculation = KsFinancialPolicy.Calculate(material, 50m,
            12.25m, 5.75m, 20m, true);
        MoneyEquals(8.5m * count + 68m, calculation.Net, "net " + count);
        MoneyEquals(decimal.Round(calculation.Net * 0.20m, 2,
            MidpointRounding.ToEven), calculation.Vat, "vat " + count);
        MoneyEquals(calculation.Net + calculation.Vat, calculation.Gross,
            "gross " + count);
    }

    private static string ObjectName(string form) => form switch
    {
        "dns" => "ДНС",
        "r4" => "Р4",
        _ => "Обычный тестовый объект"
    };

    private static SqlServerKsPrintData.Package Fixture(
        string form, int count, bool multiDs = false, string? overrideName = null)
    {
        var date = new DateTime(2026, 11, 21);
        var docs = new List<SqlServerKsContractScope.Document>
        {
            new(1, 10, "main", "42", "Договор №42", null,
                form == "standard" ? "default" : form, 20m, "", "sum")
        };
        if (multiDs)
            docs.Add(new SqlServerKsContractScope.Document(2, 10, "dop", "1",
                "ДС №1", 1, form == "standard" ? "default" : form, 20m, "", "sum"));
        var calc = KsFinancialPolicy.Calculate(count * 200m, 50m,
            12.25m, 5.75m, 20m, form == "dns");
        var acts = Enumerable.Range(0, multiDs ? 2 : 1)
            .Select(i => new SqlServerActs.ActInfo(
                11 + i, multiDs ? "1." + (i + 1) : "1",
                date.AddDays(i), date.AddDays(-20), date.AddDays(i),
                i == 0, count, calc.Gross, i == 0 ? 1 : 2,
                "Договор №42", calc.Ks3Net, calc.Vat, calc.Materials,
                calc.Smr, calc.Transport, calc.Pnr, 20m,
                form == "dns", 0, false, true)).ToList();
        var scope = new SqlServerKsContractScope.Scope(1, "42", docs, acts);
        var report = new KsGroupReport("1", "42", date, date.AddDays(-20),
            date.AddDays(multiDs ? 1 : 0),
            calc.Ks3Net * acts.Count, calc.Vat * acts.Count,
            calc.Gross * acts.Count, calc.Ks3Net * acts.Count,
            calc.Ks3Net * acts.Count, acts,
            Array.Empty<KsPreviousGroup>());
        var customer = new SqlServerKsRequisites.Values(10,
            overrideName ?? ObjectName(form), "ООО Тестовый заказчик",
            "Синтетический адрес, корпус 1", "ООО Тестовый заказчик",
            "Директор", "И.И. Иванов", "Технический заказчик",
            "ООО Тестовый технадзор", "Инженер", "П.П. Петров",
            "Монтаж систем вентиляции", "2026-01-10", "");
        var contractors = docs.ToDictionary(d => d.Id,
            d => new SqlServerKsPrintData.Contractor(d.Id, "2026-01-10",
                "ООО Тестовый подрядчик", "ООО Тестовый подрядчик",
                "Директор", "С.С. Сидоров"));
        var lines = acts.SelectMany(a => Enumerable.Range(1, count)
            .Select(i => new SqlServerKsPrintData.Line(a.Id,
                i % 2 == 0 ? "Система П1" : "Система В1",
                i == 1 ? "Изделие 1 " + new string('А', 125) : "Изделие " + i,
                "шт", i.ToString(CultureInfo.InvariantCulture),
                2m, 100m, 0m, false, "БО-1", "БО-1"))).ToList();
        return new SqlServerKsPrintData.Package(scope, report, customer,
            lines, contractors);
    }

    private static void CheckAdjustments()
    {
        foreach(var manual in new[]{false,true})
        {
            var original=Fixture("standard",2);
            var act=original.Report.Acts[0];
            var calc=KsFinancialPolicy.Calculate(act.Materials,act.Smr,act.Transport,act.Pnr,act.VatRate,false,
                discountPercent:manual?0m:5m,manualTotalCents:manual?41234:-1);
            var updated=act with {Total=calc.Gross,Vat=calc.Vat,Ks3Net=calc.Ks3Net,
                DiscountPercent=manual?0m:5m,DiscountOverride=manual,ManualTotalCents=manual?41234:-1};
            var scope=original.Scope with {Acts=new[]{updated}};
            var report=original.Report with {Acts=new[]{updated},PeriodGross=calc.Gross,PeriodVat=calc.Vat,
                PeriodNet=calc.Ks3Net,YearNet=calc.Ks3Net,SinceStartNet=calc.Ks3Net};
            using var book=KsCustomerForms.Create(original with{Scope=scope,Report=report});
            var sheet=book.Worksheets.First();
            var adjustment=sheet.CellsUsed().Single(c=>c.GetString()==(manual?"Корректировка до согласованной суммы:":"Скидка 5%:"));
            var final=sheet.CellsUsed().Single(c=>c.GetString()==(manual?"ВСЕГО С УЧЁТОМ КОРРЕКТИРОВКИ:":"ВСЕГО С УЧЁТОМ СКИДКИ:"));
            var finalCell=sheet.Cell(final.Address.RowNumber,8);
            Assert(sheet.Cell(adjustment.Address.RowNumber,8).FormulaA1.Contains("ROUND(",StringComparison.Ordinal),"adjustment must be formula");
            MoneyEquals(calc.Gross,(decimal)finalCell.GetDouble(),"KS2 discounted gross");
            MoneyEquals(calc.Gross,(decimal)book.Worksheets.Last().Cell("G29").GetDouble(),"KS3 discounted gross");
            // A manual total remains fixed even when the preceding formula changes.
            var quantity=sheet.CellsUsed().First(c=>c.GetString().StartsWith("Изделие 1",StringComparison.Ordinal));
            sheet.Cell(quantity.Address.RowNumber,6).Value=3;
            MoneyEquals(manual?calc.Gross:calc.Gross+95m,(decimal)finalCell.GetDouble(),"recalculated payable");
        }
    }

    private static void CheckSectionAmounts()
    {
        var original=Fixture("standard",2);
        var act=original.Report.Acts[0] with
        {
            SmrBySystem=true,
            SectionAmountsJson="""{"smr":{"101":10,"102":15,"103":15,"0":10},"transport":{"101":2.25,"103":5,"0":5}}"""
        };
        var package=original with
        {
            Scope=original.Scope with {Acts=new[]{act}},
            Report=original.Report with {Acts=new[]{act}},
            Lines=original.Lines.Select((line,i)=>line with {SystemId=101+i,Section="Одинаковое название"}).ToList(),
            Sections=new SqlServerKsPrintData.Section[]
            {
                new(101,act.ContractId,"Одинаковое название"),
                new(102,act.ContractId,"Одинаковое название"),
                new(103,act.ContractId,"Раздел без материалов"),
                new(999,999,"Чужой договор")
            }
        };
        var sections=package.SectionsFor(act);
        Assert(sections.Count==3,"three sections, including empty allocated section");
        Assert(sections[0].Lines.Count==1&&sections[1].Lines.Count==1&&sections[2].Lines.Count==0,
            "group by ID, not name");
        MoneyEquals(40m,sections.Sum(s=>s.Smr),"distributed SMR");
        MoneyEquals(7.25m,sections.Sum(s=>s.Transport),"distributed delivery");
        using var book=KsCustomerForms.Create(package);
        var sheet=book.Worksheets.First();
        var smr=sheet.CellsUsed().Where(c=>c.GetString()=="СМР:").ToList();
        var delivery=sheet.CellsUsed().Where(c=>c.GetString()=="ТРАНСПОРТНЫЕ РАСХОДЫ:").ToList();
        Assert(smr.Count==3&&delivery.Count==2,"section rows");
        MoneyEquals(40m,smr.Sum(c=>(decimal)sheet.Cell(c.Address.RowNumber,8).GetDouble()),"printed SMR");
        MoneyEquals(7.25m,delivery.Sum(c=>(decimal)sheet.Cell(c.Address.RowNumber,8).GetDouble()),"printed delivery");
        var workTotal=sheet.CellsUsed().Single(c=>c.GetString()=="СТОИМОСТЬ РАБОТ:");
        var deliveryTotal=sheet.CellsUsed().Single(c=>c.GetString()=="ИТОГО ТРАНСПОРТНЫЕ РАСХОДЫ:");
        MoneyEquals(act.Smr,(decimal)sheet.Cell(workTotal.Address.RowNumber,8).GetDouble(),"SMR including remainder");
        MoneyEquals(act.Transport,(decimal)sheet.Cell(deliveryTotal.Address.RowNumber,8).GetDouble(),"delivery including remainder");
        MoneyEquals(act.Total,(decimal)book.Worksheets.Last().Cell("G29").GetDouble(),"KS3 no double counting");
        var pdf=KsPdfForms.Create(package);
        using var stream=new MemoryStream(pdf);
        using var loaded=PdfReader.Open(stream,PdfDocumentOpenMode.Import);
        Assert(loaded.PageCount>=2,"section PDF opens");
    }

    private static void CheckDocuments(string form, int count, bool includePdf)
    {
        var package = Fixture(form, count);
        using (var book = KsCustomerForms.Create(package))
        {
            Assert(book.Worksheets.Count == 2, "Expected one KS2 and one KS3");
            var act = book.Worksheets.Single(w =>
                w.Name.StartsWith("кс-2", StringComparison.OrdinalIgnoreCase));
            var summary = book.Worksheets.Single(w =>
                w.Name.StartsWith("кс-3", StringComparison.OrdinalIgnoreCase));
            Assert(act.CellsUsed().Any(c =>
                c.GetString().Contains("Изделие 1", StringComparison.Ordinal)),
                "First KS2 line was lost");
            Assert(act.CellsUsed().Any(c =>
                c.GetString().Contains("Заказчик", StringComparison.Ordinal)),
                "Customer details missing");
            Assert(act.CellsUsed().Any(c =>
                !string.IsNullOrEmpty(c.FormulaA1)),
                "No KS2 monetary formula");
            var totalCell = form == "dns" ? "G32" : "G29";
            MoneyEquals(package.Report.PeriodGross,
                (decimal)summary.Cell(totalCell).GetDouble(),
                "KS3 " + form + " period gross");
            Roundtrip(book, form + "_KS2_KS3_" + count + ".xlsx", count == 2);
        }

        using (var vor = KsVorForms.Create(package, package.Report.Acts[0].Id))
        {
            var ws = vor.Worksheets.First();
            var target = ws.CellsUsed().FirstOrDefault(c =>
                c.GetString().Contains("Изделие 1", StringComparison.Ordinal));
            Assert(target is not null, "VOR has no first position");
            var quantityColumn = form switch
            {
                "dns" => 6, "r4" => 8, _ => 4
            };
            MoneyEquals(2m, (decimal)ws.Cell(target.Address.RowNumber,
                quantityColumn).GetDouble(), "VOR position 1 quantity");
            Assert(ws.Row(target.Address.RowNumber).Height > 20,
                "Long VOR position has insufficient row height");
            Roundtrip(vor, form + "_VOR_" + count + ".xlsx", count == 2);
        }

        if (includePdf)
        {
            var pdf = KsPdfForms.Create(package);
            Assert(pdf.Length > 1500 && pdf[0] == (byte)'%' &&
                pdf[1] == (byte)'P' && pdf[2] == (byte)'D' &&
                pdf[3] == (byte)'F', "PDF magic/length invalid");
            using var stream = new MemoryStream(pdf);
            using var loaded = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            Assert(loaded.PageCount >= 2, "KS2 and KS3 require PDF pages");
            var output = SamplePath(form + "_KS2_KS3.pdf");
            if (output is not null) File.WriteAllBytes(output, pdf);
        }
    }

    private static void CheckMultiDs(string form)
    {
        var package = Fixture(form, 2, multiDs: true);
        Assert(package.Scope.Documents.Count == 2 &&
            package.Report.Acts[0].Signed && !package.Report.Acts[1].Signed,
            "Fixture must cover main+DS and signed+unsigned");
        using var book = KsCustomerForms.Create(package);
        Assert(book.Worksheets.Count == 3, "Two KS2 sheets plus one KS3 required");
        var summary = book.Worksheets.Single(w =>
            w.Name.StartsWith("кс-3", StringComparison.OrdinalIgnoreCase));
        Assert(summary.CellsUsed().Any(c =>
            c.FormulaA1.Contains("кс-2", StringComparison.OrdinalIgnoreCase)),
            "Grouped KS3 must link both KS2 sheets");
        MoneyEquals(package.Report.PeriodGross,
            (decimal)summary.Cell(form == "dns" ? "G32" : "G29").GetDouble(),
            "Multi-DS group gross");
        Roundtrip(book, form + "_MULTI_DS.xlsx", writeArtifact: true);
    }

    private static void CheckOpening(string form)
    {
        var original = Fixture(form, 2);
        var package = original with
        {
            Report = original.Report with
            {
                SinceStartNet = 1289.45m,
                YearNet = 1089.45m,
                GrossCumulative = true,
                Previous = new[] { new KsPreviousGroup("0",
                    new DateTime(2026, 10, 20), 100m, 20m, 120m) }
            }
        };
        using var book = KsCustomerForms.Create(package);
        var summary = book.Worksheets.Single(w =>
            w.Name.StartsWith("кс-3", StringComparison.OrdinalIgnoreCase));
        MoneyEquals(1289.45m, (decimal)summary.Cell(
            form == "dns" ? "D29" : "D26").GetDouble(),
            "KS3 inception gross opening");
        MoneyEquals(1089.45m, (decimal)summary.Cell(
            form == "dns" ? "F29" : "F26").GetDouble(),
            "KS3 current-year gross opening");
        Roundtrip(book, form + "_OPENING.xlsx", writeArtifact: true);
    }

    private static void CheckPythonOracle(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert(root.GetProperty("oracle").GetString() == "archived-python-smeta.db.Act",
            "Oracle must come from archived Python Act model");
        var cases = root.GetProperty("cases");
        Assert(cases.GetArrayLength() >= 6, "Required Python money cases are missing");
        foreach (var item in cases.EnumerateArray())
        {
            var label = item.GetProperty("name").GetString() ?? "unnamed";
            var amounts = item.GetProperty("rows").EnumerateArray()
                .Select(x => (Quantity: x.GetProperty("qty").GetDecimal(),
                    Price: x.GetProperty("price").GetDecimal())).ToArray();
            var exact = item.GetProperty("rows_exact").GetBoolean();
            var roundRows = item.GetProperty("round_rows").GetBoolean();
            var material = KsFinancialPolicy.Materials(amounts, exact, roundRows);
            MoneyEquals(item.GetProperty("materials").GetDecimal(), material,
                label + " Python materials");
            var calculated = KsFinancialPolicy.Calculate(material,
                item.GetProperty("smr").GetDecimal(),
                item.GetProperty("transport").GetDecimal(),
                item.GetProperty("pnr").GetDecimal(),
                item.GetProperty("vat_rate").GetDecimal(),
                item.GetProperty("form").GetString() == "dns",
                item.GetProperty("adjustment_cents").GetInt64());
            MoneyEquals(item.GetProperty("net").GetDecimal(), calculated.Net,
                label + " Python net");
            MoneyEquals(item.GetProperty("total").GetDecimal(), calculated.Gross,
                label + " Python gross");
            MoneyEquals(item.GetProperty("vat").GetDecimal(), calculated.Vat,
                label + " Python VAT");
        }
    }

    private static void CheckDnsRoundRows()
    {
        var original = Fixture("dns", 2);
        var lines = original.Lines.Select(x => x with
            { Quantity = 1m, Price = 0.335m }).ToList();
        var materials = KsFinancialPolicy.Materials(
            lines.Select(x => (x.Quantity, x.Price)), false, true);
        MoneyEquals(0.68m, materials, "DNS rounded materials");
        var calc = KsFinancialPolicy.Calculate(materials, 50m,
            12.25m, 5.75m, 20m, true);
        var act = original.Report.Acts[0] with
        {
            Materials = calc.Materials, Ks3Net = calc.Ks3Net,
            Total = calc.Gross, Vat = calc.Vat
        };
        var revised = original with
        {
            Lines = lines,
            Scope = original.Scope with { Acts = new[] { act } },
            Report = original.Report with
            {
                Acts = new[] { act }, PeriodNet = calc.Ks3Net,
                PeriodGross = calc.Gross, PeriodVat = calc.Vat,
                YearNet = calc.Ks3Net, SinceStartNet = calc.Ks3Net
            }
        };
        using var book = KsCustomerForms.Create(revised);
        var ks2 = book.Worksheets.First(w =>
            w.Name.StartsWith("кс-2", StringComparison.OrdinalIgnoreCase));
        Assert(ks2.CellsUsed().Count(c =>
            c.FormulaA1.StartsWith("ROUND(F", StringComparison.OrdinalIgnoreCase)) >= 2,
            "DNS round_rows must print rounded individual position formulas");
        var ks3 = book.Worksheets.First(w =>
            w.Name.StartsWith("кс-3", StringComparison.OrdinalIgnoreCase));
        MoneyEquals(calc.Gross, (decimal)ks3.Cell("G32").GetDouble(),
            "DNS half-kopeck reconciled KS3 gross");
    }

    private static void CheckR4PositionSmr()
    {
        var original = Fixture("r4", 2);
        var doc = original.Scope.Documents[0] with { SmrMode = "pos" };
        var lines = original.Lines.Select((x, i) =>
            x with { WorkPrice = i == 0 ? 25m : 0m }).ToList();
        var package = original with
        {
            Lines = lines,
            Scope = original.Scope with { Documents = new[] { doc } }
        };
        using var book = KsCustomerForms.Create(package);
        var ks2 = book.Worksheets.Single(w =>
            w.Name.StartsWith("кс-2", StringComparison.OrdinalIgnoreCase));
        Assert(ks2.CellsUsed().Any(c =>
            c.GetString().Contains("Монтаж: Изделие 1", StringComparison.Ordinal)),
            "R4 positional SMR must appear as a separate work line");
        var ks3 = book.Worksheets.Single(w =>
            w.Name.StartsWith("кс-3", StringComparison.OrdinalIgnoreCase));
        MoneyEquals(package.Report.PeriodGross,
            (decimal)ks3.Cell("G29").GetDouble(), "R4 positional SMR KS3");
    }

    private static void CheckDneprovsky()
    {
        var package = Fixture("standard", 2, overrideName: "Днепровский");
        try
        {
            using var book = KsCustomerForms.Create(package);
            throw new InvalidDataException("Customer KS form silently fell back");
        }
        catch (NotSupportedException) { /* expected fail-closed */ }
        try
        {
            var pdf = KsPdfForms.Create(package);
            throw new InvalidDataException("Customer PDF silently fell back: " + pdf.Length);
        }
        catch (NotSupportedException) { /* expected fail-closed */ }
        // VOR currently maps Dneprovsky to the standard VOR workbook,
        // matching Python export_vor's default dispatch. Customer approval
        // is missing; this is NOT a native Dneprovsky golden pass.
    }

    private static string? SamplePath(string name)
    {
        var directory = Environment.GetEnvironmentVariable("GMC_KS_GOLDEN_OUTPUT");
        if (string.IsNullOrWhiteSpace(directory)) return null;
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    private static void Roundtrip(XLWorkbook book, string artifactName, bool writeArtifact)
    {
        using var data = new MemoryStream();
        book.SaveAs(data);
        Assert(data.Length > 2000, "XLSX package unexpectedly small");
        if (writeArtifact)
        {
            var output = SamplePath(artifactName);
            if (output is not null) File.WriteAllBytes(output, data.ToArray());
        }
        data.Position = 0;
        using var reopened = new XLWorkbook(data);
        Assert(reopened.Worksheets.Count == book.Worksheets.Count,
            "XLSX roundtrip lost worksheets");
        foreach (var sheet in reopened.Worksheets)
        {
            Assert(sheet.PageSetup.PrintAreas.Any(),
                "Missing print area after XLSX save/reopen: " + sheet.Name);
            Assert(sheet.CellsUsed().Any(),
                "Empty worksheet after XLSX save/reopen: " + sheet.Name);
        }
    }
}
