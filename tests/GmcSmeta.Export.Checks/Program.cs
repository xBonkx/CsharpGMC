using System.IO;
using ClosedXML.Excel;
using PdfSharp.Pdf.IO;
using GmcSmeta.App;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

internal static class Program
{
    /// <summary>End-to-end offline checks: SQL snapshots are synthetic, no DB connection.</summary>
    [STAThread]
    private static int Main()
    {
        try
        {
            var samples=Environment.GetEnvironmentVariable("GMC_EXPORT_SAMPLE_DIR");
            if(!string.IsNullOrWhiteSpace(samples)) Directory.CreateDirectory(samples);
            ProcurementExportChecks.Run(samples);
            foreach (var (form, name) in new[]
            {
                ("default","Обычный объект"),("dns","ДНС"),("r4","Р4")
            })
            {
                foreach(var count in new[]{2,80})
                {
                    var package=Fixture(form,name,count);
                    using(var ks=KsCustomerForms.Create(package))
                    {
                        Assert(ks.Worksheets.Count==2,"Expected KS-2 and KS-3, "+form);
                        Assert(ks.Worksheets.Any(w=>w.Name.StartsWith("кс-2",StringComparison.OrdinalIgnoreCase)),
                            "KS-2 missing");
                        Assert(ks.Worksheets.Any(w=>w.Name.StartsWith("кс-3",StringComparison.OrdinalIgnoreCase)),
                            "KS-3 missing");
                        var act=ks.Worksheets.First(w=>w.Name.StartsWith("кс-2",StringComparison.OrdinalIgnoreCase));
                        Assert(act.CellsUsed().Any(c=>c.GetString().Contains("Заказчик")),
                            "Customer requisites missing: "+form);
                        Assert(act.CellsUsed().Any(c=>c.GetString().Contains("Система В1")),
                            "Section heading missing: "+form);
                        Assert(act.CellsUsed().Any(c=>c.GetString().Contains("Изделие")),
                            "Act lines missing: "+form);
                        if(form=="dns"||form=="r4")
                            Assert(act.CellsUsed().Any(c=>!string.IsNullOrEmpty(c.FormulaA1)),
                                "No line/totals formulas for "+form);
                        var ks3=ks.Worksheets.Single(w=>w.Name.StartsWith("кс-3",StringComparison.OrdinalIgnoreCase));
                        var grossCell=ks3.Cell(form=="dns"?"G32":"G29");
                        var actualGross=grossCell.GetDouble();
                        Assert(Math.Abs(actualGross-(double)package.Report.PeriodGross)<0.011,
                            "KS-3 gross differs from authoritative SQL amount: "+form+
                            " actual="+actualGross.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        Roundtrip(ks, count==2 && !string.IsNullOrWhiteSpace(samples)
                            ? Path.Combine(samples,form+"_KS2_KS3.xlsx") : null);
                    }
                    var pdf=KsPdfForms.Create(package);
                    Assert(pdf.Length>1500,"PDF file unexpectedly small: "+form);
                    Assert(System.Text.Encoding.ASCII.GetString(pdf,0,5)=="%PDF-",
                        "PDF signature invalid: "+form);
                    using(var pdfStream=new MemoryStream(pdf))
                    using(var checkedPdf=PdfReader.Open(pdfStream,PdfDocumentOpenMode.Import))
                        Assert(checkedPdf.PageCount>=2,"KS PDF must contain an act and a group summary");
                    if(count==2 && !string.IsNullOrWhiteSpace(samples))
                        File.WriteAllBytes(Path.Combine(samples,form+"_KS2_KS3.pdf"),pdf);
                    using(var vor=KsVorForms.Create(package,1))
                    {
                        Assert(vor.Worksheets.Count>=1,"VOR missing");
                        var sheet=vor.Worksheets.First();
                        Assert(sheet.CellsUsed().Any(c=>c.GetString().Contains("Ведомость")),
                            "VOR header missing: "+form);
                        Assert(sheet.CellsUsed().Any(c=>c.GetString().Contains("Изделие")),
                            "VOR rows missing: "+form);
                        Roundtrip(vor, count==2 && !string.IsNullOrWhiteSpace(samples)
                            ? Path.Combine(samples,form+"_VOR.xlsx") : null);
                    }
                    Console.WriteLine("PASS "+form+" "+count+" lines: KS/VOR XLSX reopen and PDF header");
                }
            }
            foreach(var (form, name) in new[]
            {
                ("default","Обычный объект"),("dns","ДНС"),("r4","Р4")
            })
            {
                var original=Fixture(form,name,2);
                var first=original.Report.Acts[0];
                var second=first with { Id=2, Number="2", Date=first.Date.AddDays(1) };
                var acts=new[]{first,second};
                var merged=original with
                {
                    Scope=original.Scope with {Acts=acts},
                    Report=original.Report with
                    {
                        Key="1-2", Acts=acts,
                        PeriodNet=first.Ks3Net*2,
                        PeriodVat=first.Vat*2,
                        PeriodGross=first.Total*2,
                        YearNet=first.Ks3Net*2,
                        SinceStartNet=first.Ks3Net*2
                    },
                    Lines=original.Lines.Concat(original.Lines.Select(x=>x with {ActId=2})).ToList()
                };
                using var book=KsCustomerForms.Create(merged);
                Assert(book.Worksheets.Count==3,"Two-act group must have 3 sheets: "+form);
                var ks2=book.Worksheets.Where(x=>x.Name.StartsWith("кс-2",StringComparison.OrdinalIgnoreCase)).ToList();
                Assert(ks2.Count==2 && ks2.Select(x=>x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()==2,
                    "KS-2 sheet names must be unique: "+form);
                var summary=book.Worksheets.Single(x=>x.Name.StartsWith("кс-3",StringComparison.OrdinalIgnoreCase));
                Assert(summary.CellsUsed().Any(x=>x.FormulaA1.Contains("кс-2",StringComparison.OrdinalIgnoreCase)),
                    "Grouped KS-3 must link individual acts: "+form);
                Roundtrip(book);
                Console.WriteLine("PASS "+form+" grouped multi-act KS-3 formulas");
            }

            foreach (var (form,name,startCell,yearCell) in new[]
            {
                ("dns","ДНС","D29","F29"),("r4","Р4","D26","F26")
            })
            {
                var original=Fixture(form,name,2);
                var grossOpening=original with
                {
                    Report=original.Report with
                    {
                        SinceStartNet=1289.45m,
                        YearNet=1089.45m,
                        GrossCumulative=true
                    }
                };
                using var workbook=KsCustomerForms.Create(grossOpening);
                var summary=workbook.Worksheets.Single(x=>
                    x.Name.StartsWith("кс-3",StringComparison.OrdinalIgnoreCase));
                Assert(Math.Abs(summary.Cell(startCell).GetDouble()-1289.45)<0.011,
                    "Gross opening inception amount lost in "+form);
                Assert(Math.Abs(summary.Cell(yearCell).GetDouble()-1089.45)<0.011,
                    "Gross opening year amount lost in "+form);
                Roundtrip(workbook);
            }
            Console.WriteLine("PASS DNS/R4 initial gross opening and print areas");

            var external=Fixture("default","Днепровский",2);
            try
            {
                using var unused=KsCustomerForms.Create(external);
                throw new Exception("Dneprovsky external template must not silently fall back");
            }
            catch(NotSupportedException) { /* expected */ }
            Console.WriteLine("PASS external-template safety");
            return 0;
        }
        catch(Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Roundtrip(XLWorkbook book, string? samplePath = null)
    {
        using var data=new MemoryStream();
        book.SaveAs(data);
        Assert(data.Length>2000,"XLSX too small");
        if (!string.IsNullOrWhiteSpace(samplePath))
            File.WriteAllBytes(samplePath, data.ToArray());
        data.Position=0;
        using var loaded=new XLWorkbook(data);
        Assert(loaded.Worksheets.Count>0,"Invalid XLSX package");
        foreach (var sheet in loaded.Worksheets)
        {
            Assert(sheet.PageSetup.PrintAreas.Any(),
                "Missing XLSX print area after roundtrip: "+sheet.Name);
            Assert(sheet.CellsUsed().Any(),"Empty worksheet after roundtrip: "+sheet.Name);
        }
    }

    private static SqlServerKsPrintData.Package Fixture(string form,string objectName,int count)
    {
        var date=new DateTime(2026,11,21);
        var customer=new SqlServerKsRequisites.Values(10,objectName,"ООО Заказчик",
            "Стройка, корпус 1","ООО Заказчик","Директор","И.И. Иванов",
            "Техзаказчик","ООО Техконтроль","Главный инженер","П.П. Петров",
            "Монтаж систем вентиляции","2026-01-10","");
        var doc=new SqlServerKsContractScope.Document(1,10,"main",
            "42","Договор №42",null,form,20m,"");
        var vatTop=form=="dns";
        var materials=count*200m;
        var smr=50m;
        var totalBeforeVat=materials+smr;
        var vat=vatTop
            ?decimal.Round(totalBeforeVat*.20m,2,MidpointRounding.AwayFromZero)
            :decimal.Round(totalBeforeVat*20m/120m,2,MidpointRounding.AwayFromZero);
        var gross=vatTop?totalBeforeVat+vat:totalBeforeVat;
        var net=gross-vat;
        var act=new SqlServerActs.ActInfo(1,"1",date,date.AddDays(-20),date,
            false,count,gross,1,"Договор №42",net,vat,materials,smr,0m,0m,20m,vatTop);
        var report=new KsGroupReport("1","42",date,date.AddDays(-20),date,
            net,vat,gross,net,net,new[]{act},Array.Empty<KsPreviousGroup>());
        var scope=new SqlServerKsContractScope.Scope(1,"42",new[]{doc},new[]{act});
        var contractors=new Dictionary<long,SqlServerKsPrintData.Contractor>
        {
            [1]=new(1,"2026-01-01","ГМС","ООО ГМС","Директор","С.С. Сидоров")
        };
        var lines=Enumerable.Range(1,count).Select(i=>new SqlServerKsPrintData.Line(
            1,"Система В1","Изделие "+i,"шт",i.ToString(),2m,100m,0m,false,"БО-1","БО-1")).ToList();
        return new SqlServerKsPrintData.Package(scope,report,customer,lines,contractors);
    }
    private static void Assert(bool ok,string message)
    {
        if(!ok) throw new InvalidDataException(message);
    }
}
