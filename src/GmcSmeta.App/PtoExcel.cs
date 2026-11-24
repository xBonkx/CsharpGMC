using System.IO.Compression;
using System.Xml.Linq;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Maps the original Python openpyxl cell contract to the supplied Excel templates.
/// Never substitutes a new blank table for the customer's AOSR or registry layout.
/// </summary>
public static class PtoExcel
{
    private static readonly Dictionary<string,string[]> Blocks=new(StringComparer.Ordinal)
    {
        ["object"]=["Y1","A2"],["customer"]=["A5","A6","A7"],
        ["contractor"]=["Y9","A10"],["designer"]=["AL12","A13","A14","A15"],
        ["customer_rep"]=["A22","A23"],["contractor_rep"]=["A26"],
        ["control_rep"]=["A30"],["designer_rep"]=["A33"],["worker_rep"]=["A36"],
        ["executor"]=["AA38"],["works"]=["A42"],["project"]=["A45"],
        ["materials"]=Enumerable.Range(48,8).Select(x=>"A"+x).ToArray(),
        ["evidence"]=["A58"],["standards"]=["A63","A64","A65"],
        ["next_works"]=["A68"],["extra"]=["S70"],
        ["customer_sign"]=["A89"],["contractor_sign"]=["A92"],
        ["control_sign"]=["A96"],["designer_sign"]=["A99"],
        ["worker_sign"]=["A102"],["other_rep"]=["A105"],
        ["attachments"]=Enumerable.Range(76,9).Select(x=>"A"+x).ToArray()
    };
    private static readonly (string Field,string Day,string Month,string Century,string Year)[] Dates=
    [
        ("date","AL19","AP19","AY19","BB19"),
        ("start","U60","Y60","AH60","AK60"),
        ("finish","U61","Y61","AH61","AK61")
    ];
    private static readonly string[] Months=
    ["","января","февраля","марта","апреля","мая","июня","июля","августа","сентября",
     "октября","ноября","декабря"];
    private const string AosrResource="GmcSmeta.App.Templates.pto.aosr.xlsx";
    private const string RegistryResource="GmcSmeta.App.Templates.pto.registry.xlsx";
    public sealed record Output(string Aosr,string Registry,string? QualityRegistry,
        string SchemesRegistry,string CertificatesRegistry,string DeliveryChecklist);

    private static XLWorkbook Load(string kind,string? customTemplate=null)
    {
        XLWorkbook book;
        if(!string.IsNullOrWhiteSpace(customTemplate))
        {
            using var source=File.OpenRead(customTemplate);
            var templateStream=new MemoryStream();
            source.CopyTo(templateStream);
            templateStream.Position=0;
            RemoveBrokenDefinedNames(templateStream);
            book=new XLWorkbook(templateStream);
        }
        else
        {
            using var source=Assembly.GetExecutingAssembly().GetManifestResourceStream(
                kind=="aosr"?AosrResource:RegistryResource)
                ?? throw new FileNotFoundException("В сборке отсутствует исходный шаблон ПТО: "+kind);
            // ClosedXML may read embedded source parts again during SaveAs.
            // Keep its independent stream open until workbook disposal.
            var workbookStream=new MemoryStream();
            source.CopyTo(workbookStream);
            workbookStream.Position=0;
            RemoveBrokenDefinedNames(workbookStream);
            book=new XLWorkbook(workbookStream);
        }
        var ws=book.Worksheets.First();
        if(kind=="aosr")
        {
            if(ws.Cell("A18").GetString()!="освидетельствования скрытых работ" ||
               !ws.MergedRanges.Any(r=>r.RangeAddress.ToString().Contains("A42:BE42",StringComparison.Ordinal)))
            {book.Dispose();throw new InvalidDataException("Нужен исходный шаблон АОСР с разметкой Python.");}
        }
        else if(ws.Cell("A4").GetString()!="№ п/п" || ws.Cell("B4").GetString()!="Наименование документа")
        {book.Dispose();throw new InvalidDataException("Неверный шаблон реестра, нужен оригинал.");}
        foreach(var other in book.Worksheets.Skip(1).ToArray())other.Delete();
        ws.Name=kind=="aosr"?"АОСР":"Реестр";
        return book;
    }

    /// <summary>
    /// Some historical Python source templates have stale Excel defined-names
    /// pointing at deleted sheets (for example "17"). OpenPyXL tolerates this;
    /// ClosedXML refuses to open the workbook. Drop ONLY dangling/REF names,
    /// leaving valid named print areas, original sheets, style/merge/print XML intact.
    /// </summary>
    private static void RemoveBrokenDefinedNames(MemoryStream bytes)
    {
        using(var zip=new ZipArchive(bytes,ZipArchiveMode.Update,leaveOpen:true))
        {
            var entry=zip.GetEntry("xl/workbook.xml")
                ?? throw new InvalidDataException("Нет xl/workbook.xml в шаблоне АОСР.");
            XDocument xml;
            using(var input=entry.Open())xml=XDocument.Load(input,System.Xml.Linq.LoadOptions.PreserveWhitespace);
            var ns=xml.Root?.Name.Namespace
                ?? throw new InvalidDataException("Повреждено описание книги Excel.");
            var sheets=xml.Root?.Element(ns+"sheets")?.Elements(ns+"sheet")
                .Select(x=>(string?)x.Attribute("name")??"")
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var definitions=xml.Root?.Element(ns+"definedNames");
            var dirty=false;
            if(definitions is not null)
                foreach(var definition in definitions.Elements(ns+"definedName").ToArray())
                {
                    var formula=definition.Value.Trim().TrimStart('=');
                    var reference=Regex.Match(formula,
                        @"^(?:'(?<quoted>(?:[^']|'')+)'|(?<plain>[^'!]+))!");
                    var sheet=reference.Success
                        ?(reference.Groups["quoted"].Success
                            ?reference.Groups["quoted"].Value.Replace("''","'")
                            :reference.Groups["plain"].Value)
                        :"";
                    if(formula.Contains("#REF!",StringComparison.OrdinalIgnoreCase) ||
                       (reference.Success && !sheets.Contains(sheet)))
                    {
                        definition.Remove();
                        dirty=true;
                    }
                }
            if(dirty)
            {
                entry.Delete();
                using var output=zip.CreateEntry("xl/workbook.xml",CompressionLevel.Optimal).Open();
                xml.Save(output);
            }
        }
        bytes.Position=0;
    }

    private static void Text(IXLWorksheet sheet,string address,string? text)
    {
        var cell=sheet.Cell(address);
        cell.Value=text??"";
        if(!string.IsNullOrWhiteSpace(text))
        {
            cell.Style.Alignment.WrapText=true;
            cell.Style.Alignment.Vertical=XLAlignmentVerticalValues.Top;
            var limit=address.StartsWith("A",StringComparison.Ordinal)?140:65;
            var lines=(text??"").Split('\n').Sum(line=>Math.Max(1,(line.Length+limit-1)/limit));
            sheet.Row(cell.Address.RowNumber).Height=Math.Max(sheet.Row(cell.Address.RowNumber).Height,Math.Min(400,lines*14.0));
        }
    }

    private static void ClearBlock(IXLWorksheet sheet,IEnumerable<string> addresses)
    {
        foreach(var address in addresses)sheet.Cell(address).Value="";
    }
    private static void FillBlock(IXLWorksheet sheet,IReadOnlyList<string> addresses,string? value)
    {
        ClearBlock(sheet,addresses);
        Text(sheet,addresses[0],value);
    }
    private static void ClearRegistry(IXLWorksheet sheet)
    {
        foreach(var row in sheet.RowsUsed())
            foreach(var cell in row.CellsUsed())
                if(cell.Address.RowNumber!=4)cell.Value="";
    }
    private static void RegistryRows(IXLWorksheet sheet,IReadOnlyList<string> titles)
    {
        for(var i=0;i<titles.Count;i++)
        {
            var row=5+i;
            if(row>5)
                for(var col=1;col<=3;col++)
                    sheet.Cell(row,col).Style=sheet.Cell(5,col).Style;
            sheet.Cell(row,1).Value=i+1;
            Text(sheet,"B"+row,titles[i]);
            sheet.Cell(row,3).Value=1; // column is copies, not pages
        }
    }

    /// <summary>All extra indices retain the same Python registry.xlsx layout and print margins.</summary>
    private static string CreateIndex(PtoPackage p,string target,string label,string fileStem,
        string title,IReadOnlyList<string> entries,IReadOnlyDictionary<string,string>? templates)
    {
        var file=Path.Combine(target,fileStem+"_"+label+".xlsx");
        using var book=Load("registry",Template("registry",templates));
        var ws=book.Worksheets.First();
        ClearRegistry(ws);
        Text(ws,"B1",title+" к АОСР № "+PtoRules.Text(p,"number"));
        Text(ws,"B3","Объект: "+PtoRules.Text(p,"object"));
        RegistryRows(ws,entries);
        ws.PageSetup.PrintAreas.Clear();
        ws.PageSetup.PrintAreas.Add("A1:C"+Math.Max(5,entries.Count+4));
        book.SaveAs(file);
        return file;
    }

    public static Output Generate(PtoPackage package,string target,
        IReadOnlyDictionary<string,string>? customTemplates=null)
    {
        PtoRules.ValidateShape(package);
        var files=package.Attachments.Where(f=>f.Included).OrderBy(f=>f.Order).ToList();
        var quality=files.Where(f=>PtoRules.QualityKinds.Contains(f.Kind)).ToList();
        var label=Regex.Replace(PtoRules.Text(package,"number"),@"[^\w\.-]+","_").Trim('.');
        if(label.Length==0)label="draft";
        Directory.CreateDirectory(target);
        var aosrPath=Path.Combine(target,"АОСР_"+label+".xlsx");
        var registryPath=Path.Combine(target,"Реестр_"+label+".xlsx");
        string? qualityPath=null;
        var requisites=new Dictionary<string,string>(package.Fields,StringComparer.Ordinal);
        requisites["object"]=string.Join("\n",new[]{
            PtoRules.Text(package,"object"),PtoRules.Text(package,"address")}.Where(x=>x.Length>0));
        foreach(var role in new[]{"customer","contractor","control","designer","worker"})
            requisites[role+"_sign"]=PtoRules.Text(package,role+"_rep");

        if(quality.Count>5)
        {
            var qualityTitle="Реестр документов о качестве к АОСР № "+PtoRules.Text(package,"number");
            qualityPath=Path.Combine(target,"Реестр_качества_"+label+".xlsx");
            using(var book=Load("registry",Template("registry",customTemplates)))
            {
                var ws=book.Worksheets.First();
                ws.Name="Документы о качестве";
                ClearRegistry(ws);
                Text(ws,"B1",qualityTitle+" от "+PtoRules.Text(package,"date"));
                Text(ws,"B3","Объект: "+PtoRules.Text(package,"object"));
                RegistryRows(ws,quality.Select(f=>f.Title+(f.Pages.HasValue?" ("+f.Pages+" л.)":"")).ToArray());
                ws.PageSetup.PrintAreas.Clear();
                ws.PageSetup.PrintAreas.Add("A1:C"+(quality.Count+4));
                book.SaveAs(qualityPath);
            }
            requisites["materials"]=string.Join("\n",new[]{
                PtoRules.Text(package,"materials"),qualityTitle+" (прилагается)."
            }.Where(s=>s.Length>0));
            files=files.Where(f=>!PtoRules.QualityKinds.Contains(f.Kind)).ToList();
            files.Add(new PtoAttachment {Title=qualityTitle,Kind="Реестр документов о качестве",Order=int.MaxValue});
        }
        else if(quality.Count>0)
            requisites["materials"]=string.Join("\n",new[]{
                PtoRules.Text(package,"materials")}.Concat(quality.Select(x=>x.Title)).Where(x=>x.Length>0));
        requisites["attachments"]=string.Join("\n",files.Select((f,i)=>
            $"{i+1}. {f.Title}"+(f.Pages.HasValue?$" ({f.Pages} л.)":"")));

        using(var book=Load("aosr",Template("aosr",customTemplates)))
        {
            var ws=book.Worksheets.First();
            foreach(var block in Blocks)
                FillBlock(ws,block.Value,requisites.TryGetValue(block.Key,out var value)?value:"");
            Text(ws,"C19",PtoRules.Text(package,"number"));
            Text(ws,"L73",PtoRules.Text(package,"copies"));
            foreach(var (key,day,month,century,year) in Dates)
            {
                foreach(var address in new[]{day,month,century,year})ws.Cell(address).Value="";
                if(PtoRules.Date(PtoRules.Text(package,key)) is { } date)
                {
                    Text(ws,day,date.Day.ToString("00"));
                    Text(ws,month,Months[date.Month]);
                    Text(ws,century,date.Year.ToString("0000")[..2]);
                    Text(ws,year,date.Year.ToString("0000")[2..]);
                }
            }
            book.SaveAs(aosrPath);
        }
        using(var book=Load("registry",Template("registry",customTemplates)))
        {
            var ws=book.Worksheets.First();
            ClearRegistry(ws);
            Text(ws,"B1","Реестр к АОСР № "+PtoRules.Text(package,"number")+
                " от "+PtoRules.Text(package,"date"));
            Text(ws,"B3","Объект: "+PtoRules.Text(package,"object"));
            var titles=new List<string> {
                "Акт освидетельствования скрытых работ № "+PtoRules.Text(package,"number")+
                " от "+PtoRules.Text(package,"date")};
            titles.AddRange(files.Select(f=>f.Title+(f.Pages.HasValue?$" ({f.Pages} л.)":"")));
            RegistryRows(ws,titles);
            Text(ws,"B"+(titles.Count+7),"Передал: "+PtoRules.Text(package,"handed_by"));
            Text(ws,"B"+(titles.Count+9),"Принял: "+PtoRules.Text(package,"accepted_by"));
            ws.PageSetup.PrintAreas.Clear();
            ws.PageSetup.PrintAreas.Add("A1:C"+(titles.Count+9));
            book.SaveAs(registryPath);
        }
        // Dedicated indices are generated from the exact selected attachment ordering.
        // The supplied Python registry workbook remains the visual and print template.
        var included=package.Attachments.Where(f=>f.Included).OrderBy(f=>f.Order).ToArray();
        static string WithPages(PtoAttachment a) =>
            a.Title+(a.Pages is { } n?" ("+n+" л.)":"");
        var schemesPath=CreateIndex(package,target,label,"Перечень_схем",
            "Перечень исполнительных схем",
            included.Where(a=>a.Kind=="Исполнительная схема").Select(a=>
                WithPages(a)+(a.Systems.Count>0?" — "+string.Join(", ",a.Systems):"")).ToArray(),
            customTemplates);
        var certificatesPath=CreateIndex(package,target,label,"Реестр_сертификатов",
            "Реестр сертификатов и паспортов",
            included.Where(a=>PtoRules.QualityKinds.Contains(a.Kind))
                .Select(WithPages).ToArray(),customTemplates);
        var checklist=new List<string>
        {
            Path.GetFileName(aosrPath),Path.GetFileName(registryPath),
            Path.GetFileName(schemesPath),Path.GetFileName(certificatesPath)
        };
        if(qualityPath is not null)checklist.Add(Path.GetFileName(qualityPath));
        checklist.AddRange(included.Select(WithPages));
        if(included.Any(a=>PtoRules.QualityKinds.Contains(a.Kind)))
            checklist.Add("Документы_о_качестве.pdf (объединённая копия)");
        checklist.Add("Состав_выпуска.json (опись SHA-256)");
        var deliveryPath=CreateIndex(package,target,label,"Перечень_для_сдачи",
            "Перечень документов для сдачи комплекта",checklist,customTemplates);
        return new Output(aosrPath,registryPath,qualityPath,
            schemesPath,certificatesPath,deliveryPath);
    }

    private static string? Template(string kind,IReadOnlyDictionary<string,string>? paths)
    {
        if(paths is not null && paths.TryGetValue(kind,out var path) && File.Exists(path))
            return path;
        return null;
    }

    public static void ValidateCustom(string kind,string path)
    {
        using var book=Load(kind,path);
    }

    /// <summary>
    /// Accept only the supplied Python form layout and strip variable example data.
    /// Preserves original merged cells, row geometry, styles and print settings.
    /// </summary>
    public static void CleanCustomTemplate(string kind,string source,string destination)
    {
        using var book=Load(kind,source);
        var ws=book.Worksheets.First();
        if(kind=="aosr")
        {
            foreach(var block in Blocks)ClearBlock(ws,block.Value);
            ws.Cell("C19").Value="";
            ws.Cell("L73").Value="";
            foreach(var (_,day,month,century,year) in Dates)
                foreach(var address in new[]{day,month,century,year})
                    ws.Cell(address).Value="";
        }
        else ClearRegistry(ws);
        book.SaveAs(destination);
    }
}
