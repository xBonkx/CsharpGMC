using System.IO;
using System.Windows;
using GmcSmeta.App;
using GmcSmeta.Core.Domain;
using PdfSharp.Pdf.IO;

internal static class Program
{
    private static int _passed;
    private static void Check(bool ok,string reason)
    {
        if (!ok) throw new Exception(reason);
        _passed++;
    }

    [STAThread]
    private static int Main()
    {
        var root = Environment.GetEnvironmentVariable("GMC_PASSPORTS_PDF_SAMPLE_DIR");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Path.GetTempPath(), "gmc-passport-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var p = VentPassports.Create(1, "Тестовый объект", "ул. Ленина, д. 1");
            p.System = "П1";
            p.Organization = "ООО «Инженерная наладка»";
            p.Purpose = VentPassports.DetectPurpose(p.System);
            p.Equipment[0].Project[0] = "Вентилятор ВР 80-75";
            p.Equipment[6].Project[0] = "НЕДОПУСТИМЫЙ_ТЕКСТ_ЗАВЕСЫ";
            for (var i = 0; i < 3; i++)
                p.Points.Add(new VentFlowPoint
                { Section = (i + 1).ToString(), Room = "Комната " + (i + 1),
                  Project = "20", Actual = "19" });

            var small = VentPassportPdf.Create(p);
            File.WriteAllBytes(Path.Combine(root, "small.pdf"), small.Bytes);
            using (var reader = PdfReader.Open(new MemoryStream(small.Bytes), PdfDocumentOpenMode.Import))
                Check(reader.PageCount == 2 && small.Pages == 2 && small.E2StartPage == 2,
                    "Short E.2 must remain on page 2.");
            Check(!small.Draft && small.Bytes.Length > 3000, "Complete PDF incorrectly marked as draft.");

            p.Filter = true;
            var filtered = VentPassportPdf.Create(p);
            File.WriteAllBytes(Path.Combine(root, "filtered.pdf"), filtered.Bytes);
            Check(!filtered.Draft && filtered.Pages == 2, "Filter presence changed pagination.");

            p.Filter = false;
            p.Points = Enumerable.Range(1, 45).Select(i =>
                new VentFlowPoint { Section = i.ToString(),
                    Room = "Помещение №" + i, Project = "20", Actual = "19" }).ToList();
            var longDocument = VentPassportPdf.Create(p);
            File.WriteAllBytes(Path.Combine(root, "long.pdf"), longDocument.Bytes);
            using (var reader = PdfReader.Open(new MemoryStream(longDocument.Bytes), PdfDocumentOpenMode.Import))
                Check(reader.PageCount >= 3 && longDocument.E2StartPage == 3,
                    "Long E.2 must begin on page 3 with continued table headers.");

            p.Points[0].Actual = "";
            var draft = VentPassportPdf.Create(p);
            File.WriteAllBytes(Path.Combine(root, "draft.pdf"), draft.Bytes);
            Check(draft.Draft && VentPassports.Totals(p.Points).Actual is null,
                "Blank fact must be a draft without synthesized values.");

            p.Points[0].Actual = "-20";
            try
            {
                VentPassportPdf.Create(p);
                throw new Exception("Invalid negative measurement was accepted.");
            }
            catch (ArgumentException) { _passed++; }
            p.Points[0].Actual = "";

            p.Equipment[0].Project[0] = new string('Ы', 2000);
            try
            {
                VentPassportPdf.Create(p);
                throw new Exception("Excessively long equipment description did not fail.");
            }
            catch (InvalidDataException) { _passed++; }
            Console.WriteLine($"PASSPORTS PDF CHECKS PASSED: {_passed}/7");
            Console.WriteLine("PASSPORTS_PDF_DIR=" + root);
            return 0;
        }
        catch(Exception ex)
        {
            Console.Error.WriteLine("PASSPORTS PDF CHECK FAILED after "+_passed+": "+ex);
            return 1;
        }
    }
}
