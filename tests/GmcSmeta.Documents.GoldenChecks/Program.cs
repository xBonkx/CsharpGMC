using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Xml;
using System.Xml.Linq;
using ClosedXML.Excel;
using GmcSmeta.App;
using GmcSmeta.Core.Domain;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

// Independent document contract checks. Every input is synthetic and every output is
// written beneath a unique temp directory; no connection string or customer file is read.
internal static class Program
{
    private static readonly List<(string Name, string Result, string Detail)> Results = [];
    private static string _root = "";

    [STAThread]
    private static int Main()
    {
        var parent = Environment.GetEnvironmentVariable("GMC_DOCUMENTS_GOLDEN_OUTPUT");
        if (string.IsNullOrWhiteSpace(parent))
            parent = Path.GetTempPath();
        _root = Path.Combine(parent, "document-golden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        Run("Letters: multipage PDF, HTML escape, sender and original content", Letters);
        Run("Letters: scan order, repeated export, input immutability", LetterScans);
        Run("Passports: E.1/E.2, draft, pagination and repeat", Passports);
        Run("PTO: AOSR, registry, signatures, certificates, repeat", PtoExcelGolden);
        Run("VOR: synthetic XLSX import, duplicate and invalid volume", VorGolden);
        Run("Quality PDF: bookmarks, page order and rejected mismatch", QualityPdf);
        Run("ZIP: safe layout, traversal, duplicates, missing manifest", ZipSafety);
        Run("Storage: missing/invalid templates and unwritable target", StorageFailures);
        Run("Requisites: blank signatures, dates and expired certificates", DataQuality);
        Run("DOCX: structural fixture validator (NOT product parity)", DocxFixture);

        var lines = new List<string>
        {
            "# Documents golden checks — actual execution",
            "",
            "Generated UTC: " + DateTime.UtcNow.ToString("O"),
            "All inputs are synthetic. No SQL connection was opened.",
            "",
            "| Scenario | Result | Detail |", "|---|---|---|"
        };
        foreach (var result in Results)
        {
            lines.Add("| " + result.Name.Replace("|", "/") + " | " + result.Result +
                      " | " + result.Detail.Replace("|", "/").Replace("\r", " ").Replace("\n", " ") + " |");
        }
        lines.Add("");
        lines.Add("BLOCKED: client AOSR/registry originals, Excel/Word print rendering, SMB ACL, " +
                  "physical signatures, SQL persistence/concurrency and production DOCX parity " +
                  "require separate controlled environments.");
        File.WriteAllLines(Path.Combine(_root, "RESULTS.md"), lines, new UTF8Encoding(false));
        Console.WriteLine("GMC_DOCUMENTS_GOLDEN_DIR=" + _root);
        var failed = Results.Count(x => x.Result == "FAIL");
        Console.WriteLine("DOCUMENT GOLDEN: " + (Results.Count - failed) + "/" + Results.Count +
                          " scenarios passed");
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action action)
    {
        try
        {
            action();
            Results.Add((name, "PASS", "Structural/semantic assertions passed"));
            Console.WriteLine("PASS " + name);
        }
        catch (Exception error)
        {
            Results.Add((name, "FAIL", error.GetType().Name + ": " + error.Message));
            Console.Error.WriteLine("FAIL " + name + ": " + error);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidDataException("Expected " + typeof(T).Name + ": " + message);
    }

    private static string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Sha(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void CheckPdf(string path, int minimumPages)
    {
        Require(File.Exists(path) && new FileInfo(path).Length > 300,
                "PDF was not saved: " + path);
        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Require(pdf.PageCount >= minimumPages, "Invalid PDF page count: " + path);
        Require(pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>().All(p =>
                    (double)p.Width > 50 && (double)p.Height > 50),
                "Invalid PDF page geometry: " + path);
    }

    private static void Letters()
    {
        var dir = Folder("letters");
        var flow = new FlowDocument();
        flow.Blocks.Add(new Paragraph(new Bold(new Run("Согласование — проверка кириллицы"))));
        for (var i = 0; i < 145; i++)
            flow.Blocks.Add(new Paragraph(new Run("Пункт " + i +
                ": работы по устройству скрытых воздуховодов; инженерный контроль.")));
        var body = LettersPdf.SaveBody(flow);
        var letter = new LetterDocument
        {
            Number = "21.11-01", Date = "21.11.2026",
            Subject = "Уточнение <условий> № 1",
            Sender = "ООО «Синтетический подрядчик»",
            Recipient = "Заказчику (тест)", Signer = "Инженер — подпись",
            Greeting = "Добрый день", Body = body
        };
        Require(LettersPdf.BodyText(body).Contains("Согласование"),
                "Rich-text roundtrip lost Cyrillic");
        var html = LettersPdf.Html(letter);
        Require(html.Contains("21.11-01") && html.Contains("21.11.2026") &&
                html.Contains("&lt;условий&gt;") && !html.Contains("<условий>"),
                "HTML number/date escaping contract differs");
        var path = Path.Combine(dir, "multipage.pdf");
        LettersPdf.Render(letter, path, _ => throw new Exception("Unexpected scan"));
        CheckPdf(path, 2);
        var second = Path.Combine(dir, "multipage-repeat.pdf");
        LettersPdf.Render(letter, second, _ => throw new Exception("Unexpected scan"));
        CheckPdf(second, 2);
        using var first = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        using var repeat = PdfReader.Open(second, PdfDocumentOpenMode.Import);
        Require(first.PageCount == repeat.PageCount,
                "Repeated letter PDF changed page count");
    }

    private static void BlankPdf(string path, params double[] widths)
    {
        using var pdf = new PdfDocument();
        foreach (var width in widths)
        {
            var page = pdf.AddPage();
            page.Width = width;
            page.Height = 800;
        }
        pdf.Save(path);
    }

    private static void LetterScans()
    {
        var dir = Folder("letters");
        var source1 = Path.Combine(dir, "source-a.pdf");
        var source2 = Path.Combine(dir, "source-b.pdf");
        BlankPdf(source1, 311, 312);
        BlankPdf(source2, 419);
        var hashes = new[] { Sha(source1), Sha(source2) };
        var a = new LetterAttachment { Kind = "Скан письма", Pages = 2 };
        var b = new LetterAttachment { Kind = "Скан письма", Pages = 1 };
        var letter = new LetterDocument { UseScan = true, Files = [a, b] };
        string Locate(LetterAttachment f) => f.Id == a.Id ? source1 : source2;
        var output = Path.Combine(dir, "scans.pdf");
        LettersPdf.Render(letter, output, Locate);
        CheckPdf(output, 3);
        using (var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            var widths = pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>()
                .Select(page => Math.Round((double)page.Width)).ToArray();
            Require(widths.SequenceEqual([311.0, 312.0, 419.0]),
                    "Merged scan order/geometry changed");
        }
        var repeated = Path.Combine(dir, "scans-repeat.pdf");
        LettersPdf.Render(letter, repeated, Locate);
        using (var aReader = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        using (var bReader = PdfReader.Open(repeated, PdfDocumentOpenMode.Import))
            Require(aReader.PageCount == bReader.PageCount &&
                    aReader.Pages.Cast<PdfSharp.Pdf.PdfPage>()
                        .Select(page => Math.Round((double)page.Width))
                        .SequenceEqual(bReader.Pages.Cast<PdfSharp.Pdf.PdfPage>()
                            .Select(page => Math.Round((double)page.Width))),
                    "Repeated scan export changed page count or geometry");
        Require(Sha(source1) == hashes[0] && Sha(source2) == hashes[1],
                "Source scan was modified during export");
        Expect<InvalidOperationException>(
            () => LettersPdf.Render(new LetterDocument { UseScan = true },
                Path.Combine(dir, "no-scans.pdf"), Locate),
            "No scan should not create an empty letter PDF");
    }

    private static void Passports()
    {
        var dir = Folder("passports");
        var doc = VentPassports.Create(1, "Учебный объект", "Тестовая улица, 1");
        doc.System = "П1";
        doc.Date = "21.11.2026";
        doc.Organization = "ООО «Тест-наладка»";
        doc.Points = Enumerable.Range(1, 3).Select(i => new VentFlowPoint
        {
            Section = i.ToString(), Room = "Кабинет " + i,
            Project = "20", Actual = "19"
        }).ToList();
        var small = VentPassportPdf.Create(doc);
        File.WriteAllBytes(Path.Combine(dir, "short.pdf"), small.Bytes);
        Require(!small.Draft && small.Pages == 2 && small.E2StartPage == 2,
                "Standard passport E.2/pagination contract changed");
        CheckPdf(Path.Combine(dir, "short.pdf"), 2);
        var repeat = VentPassportPdf.Create(doc);
        Require(repeat.Pages == small.Pages && repeat.Draft == small.Draft,
                "Repeated passport generation changed state");

        doc.Points = Enumerable.Range(1, 55).Select(i => new VentFlowPoint
        {
            Section = i.ToString(), Room = "Помещение " + i,
            Project = "40", Actual = "39"
        }).ToList();
        var large = VentPassportPdf.Create(doc);
        File.WriteAllBytes(Path.Combine(dir, "long.pdf"), large.Bytes);
        Require(large.Pages >= 3 && large.E2StartPage == 3,
                "Long E.2 table must start on page 3");
        CheckPdf(Path.Combine(dir, "long.pdf"), 3);

        doc.Points[0].Actual = "";
        var draft = VentPassportPdf.Create(doc);
        File.WriteAllBytes(Path.Combine(dir, "draft.pdf"), draft.Bytes);
        Require(draft.Draft && VentPassports.Totals(doc.Points).Actual is null,
                "Incomplete measurement must not be silently replaced");
        doc.Points[0].Actual = "-10";
        Expect<ArgumentException>(() => VentPassportPdf.Create(doc),
                                  "Negative measurement must be rejected");
    }

    private static PtoPackage Package()
    {
        var p = PtoRules.New(1, "Объект-фикстура", "Город N, улица 1");
        foreach (var key in PtoRules.Required) p.Fields[key] = "Синтетическое значение";
        p.Fields["number"] = "ОВ-Г-7";
        p.Fields["date"] = "21.11.2026";
        p.Fields["start"] = "01.11.2026";
        p.Fields["finish"] = "20.11.2026";
        p.Fields["works"] = "Монтаж воздуховодов П1\n" + new string('А', 280);
        p.Fields["customer_rep"] = "Представитель заказчика И.И.";
        p.Fields["contractor_rep"] = "Представитель подрядчика П.П.";
        p.Fields["copies"] = "2";
        p.Fields["handed_by"] = "Сдал: инженер";
        p.Fields["accepted_by"] = "Принял: инженер";
        for (var i = 0; i < 6; i++)
            p.Attachments.Add(new PtoAttachment
            {
                Title = "Сертификат — тест " + i,
                Kind = "Сертификат", Pages = i + 1, Order = i,
                Sha256 = i.ToString("x64")
            });
        p.Attachments.Add(new PtoAttachment
        {
            Title = "Схема П1", Kind = "Исполнительная схема", Pages = 2,
            Order = 7, Sha256 = new string('b', 64), Systems = ["П1"]
        });
        p.Attachments.Add(new PtoAttachment
        {
            Title = "Исключено из выпуска", Kind = "Сертификат", Pages = 1,
            Order = 8, Sha256 = new string('c', 64), Included = false
        });
        return p;
    }

    private static string Signature(string path)
    {
        using var wb = new XLWorkbook(path);
        var entries = wb.Worksheets.SelectMany(ws => ws.CellsUsed()
            .Select(c => ws.Name + "!" + c.Address.ToString() + "=" +
                         c.GetString() + ":" + c.FormulaA1));
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", entries))));
    }

    private static void CheckXlsx(string path)
    {
        CheckOfficePackage(path, "xl/workbook.xml", "xl/worksheets/sheet1.xml");
        using var wb = new XLWorkbook(path);
        Require(wb.Worksheets.Count > 0, "Empty generated XLSX");
        foreach (var ws in wb.Worksheets)
        {
            Require(ws.CellsUsed().Any(), "Empty worksheet: " + ws.Name);
            Require(ws.CellsUsed().Where(c => c.HasFormula)
                    .All(c => !c.FormulaA1.Contains("#REF!", StringComparison.OrdinalIgnoreCase)),
                    "Broken formula reference in " + path);
        }
    }

    private static void PtoExcelGolden()
    {
        var p = Package();
        var dir = Folder("pto");
        var one = PtoExcel.Generate(p, Path.Combine(dir, "first"));
        var two = PtoExcel.Generate(p, Path.Combine(dir, "repeat"));
        foreach (var path in new[] {one.Aosr, one.Registry, one.QualityRegistry!,
                     one.SchemesRegistry, one.CertificatesRegistry, one.DeliveryChecklist,
                     two.Aosr, two.Registry, two.QualityRegistry!, two.SchemesRegistry,
                     two.CertificatesRegistry, two.DeliveryChecklist})
            CheckXlsx(path);
        Require(one.QualityRegistry is not null && two.QualityRegistry is not null,
                "Six certificates must produce extra quality register");
        using (var wb = new XLWorkbook(one.Aosr))
        {
            var ws = wb.Worksheets.First();
            Require(ws.Cell("C19").GetString() == "ОВ-Г-7" &&
                    ws.Cell("AL19").GetString() == "21" &&
                    ws.Cell("AP19").GetString() == "ноября" &&
                    ws.Cell("U60").GetString() == "01" &&
                    ws.Cell("L73").GetString() == "2",
                    "AOSR number, date, period or copy count does not match Python cell contract");
            Require(ws.Cell("A22").GetString() == "Представитель заказчика И.И." &&
                    ws.Cell("A26").GetString() == "Представитель подрядчика П.П." &&
                    ws.Cell("A89").GetString() == "Представитель заказчика И.И.",
                    "Signatory mapping did not survive export");
            Require(ws.Cell("A42").GetString().StartsWith("Монтаж воздуховодов") &&
                    ws.Cell("A42").Style.Alignment.WrapText &&
                    ws.Row(42).Height > 14,
                    "Long work name lacks wrap/row growth");
            Require(ws.Cell("A18").GetString() == "освидетельствования скрытых работ" &&
                    ws.MergedRanges.Any(r => r.RangeAddress.ToString().Contains("A42:BE42")),
                    "Original AOSR form geometry was lost");
        }
        using (var wb = new XLWorkbook(one.Registry))
        {
            var ws = wb.Worksheets.First();
            Require(ws.Cell("A4").GetString() == "№ п/п" &&
                    ws.Cell("B5").GetString().Contains("ОВ-Г-7") &&
                    ws.Cell("B6").GetString().Contains("Схема П1") &&
                    !ws.CellsUsed().Any(c => c.GetString().Contains("Исключено из выпуска")),
                    "Registry header, included/excluded documents or order changed");
        }
        using (var wb = new XLWorkbook(one.QualityRegistry!))
            Require(wb.Worksheets.First().Cell("B10").GetString().Contains("тест 5"),
                    "Sixth certificate missing from quality registry");
        using (var wb = new XLWorkbook(one.SchemesRegistry))
            Require(wb.Worksheets.First().Cell("B5").GetString().Contains("П1"),
                    "Executive scheme/system missing from sheet index");
        using (var wb = new XLWorkbook(one.DeliveryChecklist))
            Require(wb.Worksheets.First().CellsUsed()
                    .Any(c => c.GetString().Contains("Состав_выпуска.json")),
                    "Delivery checklist lacks release manifest");
        Require(Signature(one.Aosr) == Signature(two.Aosr) &&
                Signature(one.Registry) == Signature(two.Registry) &&
                Signature(one.QualityRegistry!) == Signature(two.QualityRegistry!),
                "Repeated PТО Excel export changed logical cells/formulas");
    }

    private static void VorGolden()
    {
        var dir = Folder("vor");
        var input = Path.Combine(dir, "source.xlsx");
        using (var book = new XLWorkbook())
        {
            var ws = book.AddWorksheet("ВОР");
            ws.Cell("A1").Value = "Система";
            ws.Cell("B1").Value = "Работа";
            ws.Cell("C1").Value = "Ед.";
            ws.Cell("D1").Value = "Объем";
            ws.Cell("A2").Value = "П1"; ws.Cell("B2").Value = "Воздуховод";
            ws.Cell("C2").Value = "м²"; ws.Cell("D2").Value = "5,5";
            ws.Cell("A3").Value = "П1"; ws.Cell("B3").Value = "Воздуховод";
            ws.Cell("C3").Value = "м²"; ws.Cell("D3").Value = "5,5";
            ws.Cell("A4").Value = "В1"; ws.Cell("B4").Value = "Отрицательный";
            ws.Cell("C4").Value = "м²"; ws.Cell("D4").Value = "-1";
            ws.Cell("A5").Value = "В1"; ws.Cell("B5").Value = "Неполное";
            ws.Cell("D5").Value = "не число";
            book.SaveAs(input);
        }
        CheckXlsx(input);
        var result = PtoVor.Read(input, "ВОР", 2, new PtoVor.Columns(2, 4, 3, 1));
        Require(result.Rows.Count == 1 && result.Rows[0].Qty == "5,5" &&
                result.Rows[0].SourceRow == 2,
                "VOR ignored mapping or imported duplicate/invalid rows");
        Require(result.Warnings.Any(s => s.Contains("дубль")) &&
                result.Warnings.Any(s => s.Contains("неотрицательным")),
                "VOR ignored duplicate/negative volume warning");
        Expect<ArgumentException>(() => PtoVor.Read(input, "ВОР", 2,
                new PtoVor.Columns(2, 2)), "Duplicate column mapping");
        Expect<ArgumentException>(() => PtoVor.Read(input, "отсутствует", 2,
                new PtoVor.Columns(2, 4)), "Missing worksheet");
    }

    private static void QualityPdf()
    {
        var dir = Folder("quality");
        var first = Path.Combine(dir, "first.pdf");
        var second = Path.Combine(dir, "second.pdf");
        BlankPdf(first, 200, 201);
        BlankPdf(second, 300);
        var p = PtoRules.New(1, "Объект", "Адрес");
        p.Fields["number"] = "ОВ-9";
        var a = new PtoAttachment { Title = "Два листа", Kind = "Сертификат",
            Order = 2, Pages = 2, Sha256 = Sha(first) };
        var b = new PtoAttachment { Title = "Один лист", Kind = "Декларация",
            Order = 1, Pages = 1, Sha256 = Sha(second) };
        p.Attachments.AddRange([a, b]);
        p.Attachments.Add(new PtoAttachment
        {
            Title = "Не включать", Kind = "Сертификат", Included = false,
            Pages = 1, Sha256 = new string('d', 64)
        });
        string Locate(PtoAttachment item) => item.Id == a.Id ? first :
            item.Id == b.Id ? second : throw new InvalidDataException("Excluded attachment opened");
        var target = Path.Combine(dir, "quality-merged.pdf");
        PtoArchive.MergeQuality(p, target, Locate);
        CheckPdf(target, 3);
        using (var pdf = PdfReader.Open(target, PdfDocumentOpenMode.Import))
            Require(pdf.PageCount == 3 &&
                    Math.Abs((double)pdf.Pages[0].Width - 300) < 1 &&
                    Math.Abs((double)pdf.Pages[1].Width - 200) < 1,
                    "Merged quality document has wrong page sequence");
        a.Pages = 3;
        Expect<InvalidDataException>(
            () => PtoArchive.MergeQuality(p, Path.Combine(dir, "wrong.pdf"), Locate),
            "Declared PDF sheet count mismatch");
        Require(Sha(first) == a.Sha256 && Sha(second) == b.Sha256,
                "Quality PDF merge mutated source documents");
    }

    private static string Zip(string name, params string[] entries)
    {
        var path = Path.Combine(Folder("zip"), name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var stream = new StreamWriter(archive.CreateEntry(entry).Open());
            stream.Write("{}");
        }
        return path;
    }

    private static void ZipSafety()
    {
        var validLetter = Zip("letter-valid.zip", "letters.json", "files/test.pdf");
        LettersArchive.ValidateRestoreZip(validLetter);
        Expect<InvalidDataException>(() => LettersArchive.ValidateRestoreZip(
            Zip("letter-traversal.zip", "letters.json", "../escape.txt")),
            "Letter ZIP path traversal");
        Expect<InvalidDataException>(() => LettersArchive.ValidateRestoreZip(
            Zip("letter-duplicate.zip", "letters.json", "files/ONE.pdf", "files/one.pdf")),
            "Case-insensitive duplicate letter entries");
        Expect<InvalidDataException>(() => LettersArchive.ValidateRestoreZip(
            Zip("letter-no-manifest.zip", "files/test.pdf")),
            "Missing letter archive manifest");

        var validPto = Zip("pto-valid.zip", "pto-archive.json",
            "pto-sql-snapshot.json", "files/test.pdf");
        PtoBackup.ValidateZip(validPto);
        Expect<InvalidDataException>(() => PtoBackup.ValidateZip(
            Zip("pto-traversal.zip", "pto-archive.json",
                "pto-sql-snapshot.json", "files/../../escape.txt")),
            "PTO ZIP path traversal");
        Expect<InvalidDataException>(() => PtoBackup.ValidateZip(
            Zip("pto-duplicate.zip", "pto-archive.json",
                "pto-sql-snapshot.json", "files/A.pdf", "files/a.pdf")),
            "Case-insensitive duplicate PTO entries");
        Expect<InvalidDataException>(() => PtoBackup.ValidateZip(
            Zip("pto-no-meta.zip", "pto-archive.json")),
            "Missing PTO SQL snapshot");
    }


    private static void DataQuality()
    {
        var empty = PtoRules.New(1, "Синтетический объект", "Адрес");
        var missing = PtoRules.Check(empty);
        Require(!missing.Ready && missing.Errors.Any(s => s.Contains("подписи") ||
                    s.Contains("представителя") || s.Contains("Представитель")),
                "Missing act signatories must be reported, not inferred");
        var p = Package();
        p.Fields["start"] = "20.11.2026";
        p.Fields["finish"] = "01.11.2026";
        var invalidPeriod = PtoRules.Check(p);
        Require(invalidPeriod.Errors.Any(s => s.Contains("Начало работ позже окончания")),
                "Reversed work period was not flagged");
        p.Fields["start"] = "01.11.2026";
        p.Fields["finish"] = "20.11.2026";
        p.Fields["date"] = "19.11.2026";
        Require(PtoRules.Check(p).Errors.Any(s => s.Contains("Дата акта раньше окончания")),
                "Act predating work completion was not flagged");
        p.Fields["date"] = "21.11.2026";
        var file = p.Attachments.First();
        p.CertificateSnapshots[file.Id] = new PtoCertificate
        {
            Kind = "Сертификат", Number = "TEST-1",
            Issued = "01.01.2024", Expires = "01.01.2025",
            Verified = true, Product = "Тестовая труба"
        };
        var expiry = PtoRules.Check(p);
        Require(expiry.Warnings.Any(s => s.Contains("Срок действия истёк")),
                "Expired certificate must trigger an explicit warning");
        var lost = PtoRules.Check(p, true, _ => (false, false, null));
        Require(lost.Errors.Any(s => s.Contains("Не найден файл")),
                "Missing attachment should block file-checked issue");
        Expect<ArgumentException>(() => LettersRules.Validate(
            new LetterDocument { Date = "31.02.2026" }),
            "Invalid letter date must be rejected");
        Expect<ArgumentException>(() => LettersRules.Validate(
            new LetterDocument { Status = "Отправлено" }),
            "Letter cannot be marked sent without sending record");
    }

    private static void StorageFailures()
    {
        var dir = Folder("storage");
        var missing = Path.Combine(dir, "template-not-found.xlsx");
        Expect<FileNotFoundException>(() => PtoExcel.ValidateCustom("aosr", missing),
                                      "Missing AOSR template must fail explicitly");
        var invalid = Path.Combine(dir, "wrong-template.xlsx");
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("Неправильный бланк").Cell("A1").Value = "Not the original form";
            wb.SaveAs(invalid);
        }
        Expect<InvalidDataException>(() => PtoExcel.ValidateCustom("aosr", invalid),
            "Wrong customer template cannot silently replace original");
        var sentinel = Path.Combine(dir, "occupied");
        File.WriteAllText(sentinel, "do not overwrite", Encoding.UTF8);
        Expect<IOException>(() => PtoExcel.Generate(Package(), sentinel),
                            "Existing regular file cannot act as output directory");
        Require(File.ReadAllText(sentinel, Encoding.UTF8) == "do not overwrite",
                "Export destroyed unrelated existing file");
    }

    private static void CheckOfficePackage(string path, params string[] required)
    {
        using var file = File.OpenRead(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        Require(zip.GetEntry("[Content_Types].xml") is not null,
                "Office package content-types missing: " + path);
        foreach (var part in required)
            Require(zip.GetEntry(part) is not null, "Office package part missing: " + part);
        foreach (var part in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                                                   || e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            using var data = part.Open();
            using var reader = XmlReader.Create(data, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 20_000_000
            });
            _ = XDocument.Load(reader);
        }
    }

    private static void DocxFixture()
    {
        // There is no production DOCX exporter in the 3 subject modules on this
        // revision. This validates the independent inspector, NOT document parity.
        var dir = Folder("docx-fixture");
        var path = Path.Combine(dir, "synthetic-not-a-customer-form.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            static void Part(ZipArchive archive, string name, string value)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(),
                    new UTF8Encoding(false));
                writer.Write(value);
            }
            Part(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Part(zip, "_rels/.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Part(zip, "word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                "<w:body><w:p><w:r><w:t>Синтетический документ № 7</w:t></w:r></w:p></w:body></w:document>");
        }
        CheckOfficePackage(path, "_rels/.rels", "word/document.xml");
        using (var zip = ZipFile.OpenRead(path))
        using (var stream = zip.GetEntry("word/document.xml")!.Open())
        {
            var xml = XDocument.Load(stream);
            Require(xml.DescendantNodes().OfType<XText>().Any(x => x.Value.Contains("№ 7")),
                    "DOCX Unicode XML lost");
        }
        var corrupt = Path.Combine(dir, "corrupt.docx");
        File.WriteAllText(corrupt, "not a DOCX", Encoding.UTF8);
        Expect<InvalidDataException>(() => CheckOfficePackage(corrupt, "word/document.xml"),
                                     "Corrupt DOCX archive must fail");
    }
}
