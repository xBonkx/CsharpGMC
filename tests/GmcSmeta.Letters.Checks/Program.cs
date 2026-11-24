using System.IO;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Windows;
using System.Windows.Documents;
using GmcSmeta.App;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "gmc-letters-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passed = 0;
        try
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var flow = new FlowDocument();
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new Bold(new Run("Жирный текст — согласование")));
            paragraph.Inlines.Add(new Run(" и "));
            paragraph.Inlines.Add(new Italic(new Run("курсив")));
            flow.Blocks.Add(paragraph);
            var list = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Decimal };
            list.ListItems.Add(new ListItem(new Paragraph(new Run("Первый пункт"))));
            list.ListItems.Add(new ListItem(new Paragraph(new Run("Второй пункт"))));
            flow.Blocks.Add(list);
            for (var i = 0; i < 130; i++)
                flow.Blocks.Add(new Paragraph(new Run($"Параграф {i}. Проверка переноса на следующую страницу.")));
            var encoded = LettersPdf.SaveBody(flow);
            if (!encoded.StartsWith("xaml:") || !LettersPdf.BodyText(encoded).Contains("Первый пункт"))
                throw new Exception("Rich WPF roundtrip failed.");
            passed++;
            var d = new LetterDocument
            {
                Date = "02.11.2026", Number = "02.11-01", Subject = "Изменение решений",
                Sender = "ООО «Тест»\nГород", Recipient = "Директору — Иванову",
                Greeting = "Уважаемый коллега!", Body = encoded, Signer = "Директор — Петров"
            };
            var pdf = Path.Combine(root, "letter.pdf");
            LettersPdf.Render(d, pdf, _ => throw new Exception("Unexpected file access"));
            using (var reader = PdfReader.Open(pdf, PdfDocumentOpenMode.Import))
            {
                if (reader.PageCount < 2) throw new Exception("Long Cyrillic PDF was not paginated.");
            }
            passed++;
            var html = LettersPdf.Html(d);
            if (!html.Contains("<b>") || !html.Contains("курсив") || !html.Contains("<ol>"))
                throw new Exception("HTML lost formatting.");
            passed++;
            var scan1 = Path.Combine(root, "scan1.pdf");
            var scan2 = Path.Combine(root, "scan2.pdf");
            CreateBlank(scan1, 320);
            CreateBlank(scan2, 420);
            var first = new LetterAttachment { Kind = "Скан письма", Path = "files/scan1.pdf",
                Pages = 1, Sha256 = new string('a', 64) };
            var second = new LetterAttachment { Kind = "Скан письма", Path = "files/scan2.pdf",
                Pages = 1, Sha256 = new string('b', 64) };
            d.UseScan = true;
            d.Files.Add(first);
            d.Files.Add(second);
            var scanPdf = Path.Combine(root, "scans.pdf");
            LettersPdf.Render(d, scanPdf, f => f.Id == first.Id ? scan1 : scan2);
            using (var result = PdfReader.Open(scanPdf, PdfDocumentOpenMode.Import))
            {
                if (result.PageCount != 2 || Math.Abs((double)result.Pages[0].Width - 320) > 1 ||
                    Math.Abs((double)result.Pages[1].Width - 420) > 1)
                    throw new Exception("Scan page order or original PDF geometry changed.");
            }
            passed++;
            var png = Path.Combine(root, "sample.png");
            var pixels = Enumerable.Repeat((byte)185, 8 * 8 * 4).ToArray();
            var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(png)) encoder.Save(stream);
            var photoPdf = Path.Combine(root, "photo.pdf");
            LettersPdf.ImagePage(png, photoPdf);
            using (var imagePage = PdfReader.Open(photoPdf, PdfDocumentOpenMode.Import))
                if (imagePage.PageCount != 1 || Math.Abs(imagePage.Pages[0].Width.Point - 595) > 2 ||
                    Math.Abs(imagePage.Pages[0].Height.Point - 842) > 2)
                    throw new Exception("PNG was not fitted to one A4 PDF page.");
            passed++;
            var combined = Path.Combine(root, "combined.pdf");
            LettersPdf.Merge(new[] { pdf, scan1, scan2 }, combined);
            using (var result = PdfReader.Open(combined, PdfDocumentOpenMode.Import))
                if (result.PageCount < 4) throw new Exception("Merged PDF missed some pages.");
            passed++;
            var bad = Path.Combine(root, "bad.zip");
            using (var archive = ZipFile.Open(bad, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("letters.json").Open()))
                    writer.Write("[]");
                using var writer2 = new StreamWriter(archive.CreateEntry("../escaped.txt").Open());
                writer2.Write("bad");
            }
            try
            {
                LettersArchive.ValidateRestoreZip(bad);
                throw new Exception("Traversal ZIP was accepted.");
            }
            catch (InvalidDataException) { passed++; }
            Console.WriteLine($"LETTERS OFFLINE CHECKS PASSED: {passed}/{passed}");
            var connection = Environment.GetEnvironmentVariable("GMC_SMETA_LETTERS_TEST_SQL");
            if (!string.IsNullOrWhiteSpace(connection))
            {
                SqlServerWorkspace.RequirePilotDatabase(connection);
                var service = new SqlServerLetters(connection);
                var generated = new ConcurrentBag<LetterDocument>();
                Parallel.For(0, 2, _ => generated.Add(new SqlServerLetters(connection).Create()));
                if (generated.Select(x => x.Number).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2)
                    throw new Exception("Two SQL Server clients allocated duplicate numbers.");
                foreach (var doc in generated)
                    if (service.Get(doc.Id).Number != doc.Number)
                        throw new Exception("Read-after-write mismatch.");
                var saved = service.Get(generated.First().Id);
                var stale = service.Get(saved.Id);
                saved.Subject = "Revision A";
                service.Save(saved);
                stale.Subject = "Revision B";
                try
                {
                    service.Save(stale);
                    throw new Exception("Stale revision was not rejected.");
                }
                catch (System.Data.DBConcurrencyException) { /* expected */ }
                Console.WriteLine("LETTERS PILOT SQL CHECKS PASSED: 3/3 (two clients, persistence, stale revision).");
            }
            else Console.WriteLine("LETTERS PILOT SQL CHECKS NOT RUN: GMC_SMETA_LETTERS_TEST_SQL unset.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            Console.Error.WriteLine($"LETTERS CHECKS FAILED after {passed} successful checks.");
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort test cleanup */ }
        }
    }

    private static void CreateBlank(string path, double width)
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        page.Width = width;
        page.Height = 800;
        document.Save(path);
    }
}
