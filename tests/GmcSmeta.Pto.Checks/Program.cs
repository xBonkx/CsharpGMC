using System.IO.Compression;
using System.IO;
using ClosedXML.Excel;
using GmcSmeta.App;
using GmcSmeta.Core.Domain;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition,string message)
    {
        if(!condition)throw new Exception("PTO CHECK FAILURE: "+message);
        checks++;
    }
    [STAThread]
    private static int Main()
    {
        var root=Environment.GetEnvironmentVariable("GMC_PTO_SAMPLE_DIR")??
            Path.Combine(Path.GetTempPath(),"gmc-pto-checks-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var p=PtoRules.New(1,"Демонстрационный объект","Ленина 5");
            p.Fields["number"]="ОВ-7";p.Fields["date"]="21.11.2026";
            p.Fields["start"]="10.11.2026";p.Fields["finish"]="19.11.2026";
            p.Fields["customer"]="ООО «Заказчик»";
            p.Fields["works"]="Монтаж воздуховодов П1";
            var generated=PtoExcel.Generate(p,Path.Combine(root,"base"));
            using(var wb=new XLWorkbook(generated.Aosr))
            {
                var ws=wb.Worksheets.First();
                Check(ws.Cell("C19").GetString()=="ОВ-7","AOSR number missing");
                Check(ws.Cell("A42").GetString().Contains("Монтаж воздуховодов"),
                    "AOSR work block missing");
                Check(ws.Cell("AL19").GetString()=="21","AOSR Russian day missing");
                Check(ws.Cell("AP19").GetString()=="ноября","AOSR Russian month missing");
                Check(ws.MergedRanges.Any(r=>r.RangeAddress.ToString().Contains("A42:BE42")),
                    "AOSR original merge A42:BE42 lost");
                Check(ws.Cell("A18").GetString()=="освидетельствования скрытых работ",
                    "AOSR original template not preserved");
            }
            using(var wb=new XLWorkbook(generated.Registry))
            {
                var ws=wb.Worksheets.First();
                Check(ws.Cell("A4").GetString()=="№ п/п" &&
                    ws.Cell("B4").GetString()=="Наименование документа","Registry original headers lost");
                Check(ws.Cell("B5").GetString().Contains("ОВ-7"),"Registry doesn't list AOSR");
            }
            Check(generated.QualityRegistry is null,"Quality registry generated for zero docs");
            Check(File.Exists(generated.SchemesRegistry)&&
                  File.Exists(generated.CertificatesRegistry)&&
                  File.Exists(generated.DeliveryChecklist),
                "Scheme/certificate/handover registries not generated");
            using(var wb=new XLWorkbook(generated.DeliveryChecklist))
                Check(wb.Worksheets.First().Cell("B5").GetString().Contains("АОСР"),
                    "Handover list does not include the original AOSR.");
            var cleaned=Path.Combine(root,"custom-aosr-cleaned.xlsx");
            PtoExcel.CleanCustomTemplate("aosr",generated.Aosr,cleaned);
            using(var wb=new XLWorkbook(cleaned))
            {
                Check(wb.Worksheets.First().Cell("C19").GetString()=="",
                    "Custom AOSR copy retained previous act number.");
                Check(wb.Worksheets.First().Cell("A42").GetString()=="",
                    "Custom AOSR copy retained previous works.");
                Check(wb.Worksheets.First().Cell("A18").GetString()=="освидетельствования скрытых работ",
                    "Custom template cleanup removed original form header.");
            }

            for(int i=0;i<6;i++)
                p.Attachments.Add(new PtoAttachment{Title="Сертификат "+i,Kind="Сертификат",
                    Pages=1,Sha256=i.ToString("x64"),Order=i});
            var six=PtoExcel.Generate(p,Path.Combine(root,"six"));
            Check(six.QualityRegistry is not null && File.Exists(six.QualityRegistry),
                "Six included certificates must create quality registry");
            using(var wb=new XLWorkbook(six.QualityRegistry!))
                Check(wb.Worksheets.First().Cell("B10").GetString().Contains("Сертификат 5"),
                    "Quality registry does not enumerate all six certificates");
            p.Attachments[^1].Included=false;
            var five=PtoExcel.Generate(p,Path.Combine(root,"five"));
            Check(five.QualityRegistry is null,"Five included certificates must remain inline");
            p.Attachments.Add(new PtoAttachment{Title="Исполнительная схема П1",
                Kind="Исполнительная схема",Pages=2,Sha256=new string('b',64),
                Order=10,Systems=["П1"]});
            var withScheme=PtoExcel.Generate(p,Path.Combine(root,"scheme"));
            using(var wb=new XLWorkbook(withScheme.SchemesRegistry))
                Check(wb.Worksheets.First().Cell("B5").GetString().Contains("П1"),
                    "Scheme index does not include system mapping and sheet count");

            using(var book=new XLWorkbook())
            {
                var ws=book.AddWorksheet("ВОР");
                ws.Cell(1,1).Value="Система";ws.Cell(1,2).Value="Работа";
                ws.Cell(1,3).Value="Ед.";ws.Cell(1,4).Value="Объём";
                ws.Cell(2,1).Value="П1";ws.Cell(2,2).Value="Воздуховоды";
                ws.Cell(2,3).Value="м²";ws.Cell(2,4).Value="5,5";
                ws.Cell(3,1).Value="П1";ws.Cell(3,2).Value="Воздуховоды";
                ws.Cell(3,3).Value="м²";ws.Cell(3,4).Value="5,5";
                ws.Cell(4,2).Value="Заголовок раздела";
                ws.Cell(5,2).Value="Некорректный отрицательный объём";
                ws.Cell(5,4).Value="-5";
                book.SaveAs(Path.Combine(root,"vor.xlsx"));
            }
            var source=Path.Combine(root,"vor.xlsx");
            var vor=PtoVor.Read(source,"ВОР",2,new PtoVor.Columns(Name:2,Qty:4,Unit:3,System:1));
            Check(vor.Rows.Count==1 && vor.Rows[0].Qty=="5,5",
                "VOR duplicates not removed or mapped quantities were altered");
            Check(vor.Warnings.Any(w=>w.Contains("дубль")),"VOR duplicate exclusion warning missing");
            Check(vor.Warnings.Any(w=>w.Contains("неотрицательным")),"VOR negative volume warning missing");
            try {PtoVor.Read(source,"ВОР",2,new PtoVor.Columns(2,2));throw new Exception("duplicate mapping accepted");}
            catch(ArgumentException){checks++;}

            var first=Path.Combine(root,"first.pdf");
            var second=Path.Combine(root,"second.pdf");
            using(var pdf=new PdfDocument())
            {
                pdf.AddPage().Width=201;
                pdf.AddPage().Width=202;
                pdf.Save(first);
            }
            using(var pdf=new PdfDocument())
            {
                pdf.AddPage().Width=301;
                pdf.Save(second);
            }
            p.Attachments.Clear();p.CertificateSnapshots.Clear();
            p.Attachments.Add(new PtoAttachment{Title="One",Kind="Сертификат",
                Pages=2,Order=2,RelativePath="files/one.pdf",Sha256=PtoArchive.Sha(first)});
            p.Attachments.Add(new PtoAttachment{Title="Two",Kind="Декларация",
                Pages=1,Order=1,RelativePath="files/two.pdf",Sha256=PtoArchive.Sha(second)});
            p.Attachments.Add(new PtoAttachment{Title="Excluded",Kind="Паспорт",
                Pages=1,Order=0,Included=false,RelativePath="files/excluded.pdf",
                Sha256=new string('a',64)});
            var merged=Path.Combine(root,"Документы_о_качестве.pdf");
            PtoArchive.MergeQuality(p,merged,
                f=>f.Title=="One"?first:f.Title=="Two"?second:throw new Exception("Excluded opened"));
            using(var doc=PdfReader.Open(merged,PdfDocumentOpenMode.Import))
            {
                Check(doc.PageCount==3,"Merged PDF must contain 3 pages");
                Check(Math.Abs((double)doc.Pages[0].Width-301)<.01 &&
                      Math.Abs((double)doc.Pages[1].Width-201)<.01,
                    "PDF page ordering differs from registry");
            }
            try
            {
                p.Attachments[0].Pages=3;
                PtoArchive.MergeQuality(p,Path.Combine(root,"wrong.pdf"),
                    f=>f.Title=="One"?first:second);
                throw new Exception("Wrong PDF page count was accepted");
            }
            catch(InvalidDataException){checks++;}
            try
            {
                File.WriteAllText(Path.Combine(root,"corrupt.pdf"),"not a PDF");
                PtoArchive.PdfPages(Path.Combine(root,"corrupt.pdf"));
                throw new Exception("Corrupt PDF accepted");
            }
            catch(InvalidDataException){checks++;}
            // Validate ZIP restore metadata and security without touching any SQL database.
            string Zip(string name,Action<ZipArchive> fill)
            {
                var path=Path.Combine(root,name);
                using(var archive=ZipFile.Open(path,ZipArchiveMode.Create))
                {
                    archive.CreateEntry("pto-archive.json");
                    archive.CreateEntry("pto-sql-snapshot.json");
                    fill(archive);
                }
                return path;
            }
            var safeZip=Zip("safe.zip",zip=>zip.CreateEntry("files/valid.pdf"));
            PtoBackup.ValidateZip(safeZip);
            checks++;
            var traversalZip=Zip("traversal.zip",zip=>zip.CreateEntry("../escape.txt"));
            try
            {
                PtoBackup.ValidateZip(traversalZip);
                throw new Exception("ZIP traversal path was accepted.");
            }
            catch(InvalidDataException){checks++;}
            var duplicateZip=Zip("duplicate.zip",zip=>
            {
                zip.CreateEntry("files/same.pdf");
                zip.CreateEntry("files/same.pdf");
            });
            try
            {
                PtoBackup.ValidateZip(duplicateZip);
                throw new Exception("Duplicate archive entry was accepted.");
            }
            catch(InvalidDataException){checks++;}
            var symlinkZip=Zip("symlink.zip",zip=>
            {
                var link=zip.CreateEntry("files/link.pdf");
                link.ExternalAttributes=(0xA000<<16);
            });
            try
            {
                PtoBackup.ValidateZip(symlinkZip);
                throw new Exception("ZIP symlink entry was accepted.");
            }
            catch(InvalidDataException){checks++;}
            Console.WriteLine($"PTO WINDOWS EXCEL/VOR/PDF/ZIP CHECKS PASSED: {checks}/{checks}");
            Console.WriteLine("PTO_SAMPLES="+root);
            return 0;
        }
        catch(Exception ex)
        {
            Console.Error.WriteLine($"PTO WINDOWS CHECK FAILED after {checks}: {ex}");
            return 1;
        }
    }
}
