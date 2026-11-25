using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Export dispatcher for Python's standard / DNS / R4 forms.
/// Customer workbooks are embedded directly from python/sql-server-current/smeta/assets.
/// No runtime network share, SQL writes, or hard-coded customer company is required.
/// </summary>
public static class KsCustomerForms
{
    public enum Form { Standard, Dns, R4, Dneprovsky }

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private const string MoneyFormat = "#,##0.00";

    public static Form Resolve(SqlServerKsPrintData.Package package)
    {
        var selected = package.Report.Acts
            .Select(a => package.Scope.Documents.Single(d => d.Id == a.ContractId).Form.Trim().ToLowerInvariant())
            .Distinct().ToArray();
        var objectName = package.Requisites.ObjectName.Trim().ToLowerInvariant();
        if (objectName == "днепровский") return Form.Dneprovsky;
        if (selected.Length != 1)
            throw new InvalidOperationException("В одной КС-3 смешаны разные формы заказчиков. Нужна сверка с Python.");
        return selected[0] switch
        {
            "dns" => Form.Dns,
            "r4" when objectName is "р4" or "p4" or "r4" => Form.R4,
            _ => Form.Standard
        };
    }

    public static XLWorkbook Create(SqlServerKsPrintData.Package package) =>
        CreateAll(new[] { package });

    // Fill all report pairs directly in one workbook. Cross-workbook copying can
    // lose sheet references and customer template formatting.
    public static XLWorkbook CreateAll(IReadOnlyList<SqlServerKsPrintData.Package> packages)
    {
        if (packages.Count == 0)
            throw new InvalidOperationException("Нет актов для экспорта.");
        var form = Resolve(packages[0]);
        foreach (var package in packages)
        {
            if (Resolve(package) != form)
                throw new InvalidOperationException("В одном файле смешаны разные формы КС.");
            ValidateAdjustments(package,form);
        }
        if (form == Form.Dneprovsky)
            throw new NotSupportedException(
                "Для «Днепровского» Python использует внешний оригинальный бланк с сетевого ресурса. " +
                "Без согласованной копии шаблона экспорт запрещён: типовая форма не заменяет форму заказчика.");
        if (form == Form.Standard)
        {
            var standard = new XLWorkbook();
            try
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var package in packages)
                    KsExcelForms.Fill(standard, package, names);
                return standard;
            }
            catch { standard.Dispose(); throw; }
        }

        var book = Load(form == Form.Dns ? "ks_dns_template.xlsx" : "ks_r4_template.xlsx");
        try
        {
            var ks2 = form == Form.Dns ? book.Worksheet("KS2") : book.Worksheet(1);
            var ks3 = form == Form.Dns ? book.Worksheet("KS3") : book.Worksheet(2);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in packages)
            {
                var refs = new List<(string Name, int Total, int Vat)>();
                foreach (var act in package.Report.Acts)
                {
                    var name = UniqueName("кс-2 №" + act.Number, names);
                    var sheet = ks2.CopyTo(name);
                    var result = form == Form.Dns
                        ? FillDnsAct(sheet, package, act)
                        : FillR4Act(sheet, package, act);
                    refs.Add((name, result.Total, result.Vat));
                }
                var summary = ks3.CopyTo(UniqueName("кс-3 №" + package.Report.Key, names));
                if (form == Form.Dns) FillDnsSummary(summary, package, refs);
                else FillR4Summary(summary, package, refs);
            }
            ks2.Delete();
            ks3.Delete();
            return book;
        }
        catch
        {
            book.Dispose();
            throw;
        }
    }

    internal static void ValidateAdjustments(SqlServerKsPrintData.Package package,Form form)
    {
        foreach(var act in package.Report.Acts)
        {
            if(act.DiscountPercent==0m&&!act.DiscountOverride&&act.ManualTotalCents<0&&string.IsNullOrEmpty(act.SectionAmountsJson))continue;
            if(form!=Form.Standard||act.VatOnTop)
                throw new InvalidOperationException("Итоговая скидка и ручная разбивка сумм не поддерживаются специальной формой КС.");
        }
    }

    internal static XLWorkbook Load(string name)
    {
        var assembly = typeof(KsCustomerForms).Assembly;
        using var source = assembly.GetManifestResourceStream("GmcSmeta.App.Templates." + name)
            ?? throw new FileNotFoundException("В сборке нет исходного Excel-шаблона: " + name);
        // ClosedXML reads some parts from its original package when SaveAs is
        // later invoked. Keep an independent memory stream open for the lifetime
        // of the workbook; closing the resource stream here caused exceptions
        // on legitimate Python customer templates with embedded drawing parts.
        var backing = new MemoryStream();
        source.CopyTo(backing);
        backing.Position = 0;
        XLWorkbook workbook;
        try { workbook = new XLWorkbook(backing); }
        catch { backing.Dispose(); throw; }
        // The templates have legacy print/named ranges that ClosedXML 0.104.2
        // rewrites to an EMPTY formula when rows are deleted (issue #2590).
        // Remove names before resizing, then set explicit print areas in the
        // exported sheets; formulas here are direct cell references.
        RemoveTemplateDefinedNames(workbook);
        return workbook;
    }

    private static void RemoveTemplateDefinedNames(XLWorkbook workbook)
    {
        workbook.DefinedNames.DeleteAll();
        foreach (var sheet in workbook.Worksheets)
            sheet.DefinedNames.DeleteAll();
    }

    private static string UniqueName(string proposed, HashSet<string> names)
    {
        var clean = new string(proposed.Where(c => !":\\/?*[]".Contains(c)).ToArray());
        clean = clean[..Math.Min(clean.Length,31)];
        if (names.Add(clean)) return clean;
        for(var i=2;i<1000;i++)
        {
            var suffix="_"+i.ToString(Invariant);
            var candidate=clean[..Math.Min(clean.Length,31-suffix.Length)]+suffix;
            if(names.Add(candidate))return candidate;
        }
        throw new InvalidOperationException("Слишком много одинаковых номеров актов.");
    }
    internal static string SheetRef(string name) => "'" + name.Replace("'", "''") + "'!";
    private static string FormulaSum(IEnumerable<string> refs) =>
        "SUM(" + (string.Join(",", refs) is { Length: > 0 } items ? items : "0") + ")";
    private static string Num(decimal amount) => amount.ToString("0.00", Invariant);
    private static string Add(decimal delta) => delta == 0m ? "" : (delta > 0m ? "+" : "") + Num(delta);
    private static void DateCell(IXLWorksheet ws, string address, DateTime? value)
    {
        if (value is null) { ws.Cell(address).Clear(XLClearOptions.Contents); return; }
        ws.Cell(address).Value = value.Value;
        ws.Cell(address).Style.NumberFormat.Format = "dd.mm.yyyy";
    }
    private static void Print(IXLWorksheet ws, int end, string column = "H")
    {
        ws.PageSetup.PrintAreas.Clear();
        ws.PageSetup.PrintAreas.Add("A1:" + column + end);
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
        ws.ShowGridLines = false;
    }
    private static void FillAgreement(IXLWorksheet ws, SqlServerKsPrintData.Package p, long docId,
        string customerCell, string contractorCell, string siteCell, string numberCell)
    {
        var r = p.Requisites;
        var co = p.Contractors[docId];
        var doc = p.Scope.Documents.Single(d => d.Id == docId);
        ws.Cell(customerCell).Value = "Заказчик  - " +
            (string.IsNullOrWhiteSpace(r.CustomerFull) ? r.CustomerOrganization : r.CustomerFull);
        ws.Cell(contractorCell).Value = "Подрядчик  - " +
            (string.IsNullOrWhiteSpace(co.LegalName) ? co.CompanyName : co.LegalName);
        ws.Cell(siteCell).Value = "Стройка - " +
            (string.IsNullOrWhiteSpace(r.SiteFull) ? r.ObjectName : r.SiteFull);
        ws.Cell(numberCell).Value = p.Scope.AgreementNumber.TrimStart('№', ' ');
    }

    private static (int Total, int Vat) FillDnsAct(IXLWorksheet ws, SqlServerKsPrintData.Package p,
        SqlServerActs.ActInfo act)
    {
        FillAgreement(ws,p,act.ContractId,"A6","A8","A9","F11");
        if (!string.IsNullOrWhiteSpace(p.Requisites.SecondOrganization))
            ws.Cell("A7").Value = p.Requisites.SecondRole + ": " + p.Requisites.SecondOrganization;
        ws.Cell("E19").Value = act.Number;
        DateCell(ws,"F19",act.Date);
        DateCell(ws,"G19",act.PeriodFrom);
        DateCell(ws,"H19",act.PeriodTo);
        var lines = p.Lines.Where(l=>l.ActId==act.Id).ToList();
        var items = new List<(bool Heading, SqlServerKsPrintData.Line? Line, string Section)>();
        foreach (var group in lines.GroupBy(l=>l.Section))
        {
            if (group.Key != "Без системы")
                items.Add((true,null,"Раздел " + (items.Count(x=>x.Heading)+1) + ". " + group.Key));
            items.AddRange(group.Select(l=>(false,(SqlServerKsPrintData.Line?)l,"")));
        }
        const int first=26, footer=64;
        // Capture template styles BEFORE moving rows or signatures.
        var section = Enumerable.Range(1,8).Select(c=>ws.Cell(26,c).Style).ToArray();
        var position = Enumerable.Range(1,8).Select(c=>ws.Cell(28,c).Style).ToArray();
        var diff = items.Count-(footer-first);
        if (diff>0) ws.Row(footer).InsertRowsAbove(diff);
        else if (diff<0) ws.Rows(first+items.Count,footer-1).Delete();
        var tot=footer+diff;
        var rows = new List<string>();
        for(var i=0;i<items.Count;i++)
        {
            var row=first+i;
            var item=items[i];
            for(int c=1;c<=8;c++)
            {
                var cell=ws.Cell(row,c);
                cell.Clear(XLClearOptions.Contents);
                cell.Style=(item.Heading?section:position)[c-1];
            }
            ws.Cell(row,1).Value=i+1;
            if(item.Heading)
            {
                ws.Cell(row,3).Value=item.Section;
                ws.Cell(row,5).Value="*";
                ws.Cell(row,3).Style.Font.Bold=true;
            }
            else if(item.Line is { } line)
            {
                ws.Cell(row,2).Value=line.PositionNumber;
                ws.Cell(row,3).Value=line.Name;
                ws.Cell(row,5).Value=line.Unit;
                ws.Cell(row,6).Value=(double)line.Quantity;
                ws.Cell(row,7).Value=(double)line.Price;
                ws.Cell(row,8).FormulaA1=(act.RowsExact || !act.RoundRows
                    ? "F"+row+"*G"+row
                    : "ROUND(F"+row+"*G"+row+",2)");
                ws.Range(row,7,row,8).Style.NumberFormat.Format=MoneyFormat;
                rows.Add("H"+row);
                ws.Row(row).Height=Math.Max(18.75,Math.Ceiling(line.Name.Length/85d)*14+4);
            }
        }
        // Reconcile rows with the same authoritative SQL Server figures used by the UI.
        // This also captures SMR, transport, PNR, special rounding and gross adjustment.
        var lineNet=lines.Sum(l=>act.RowsExact || !act.RoundRows
            ? l.Quantity*l.Price
            : decimal.Round(l.Quantity*l.Price,2,MidpointRounding.AwayFromZero));
        var delta=act.Ks3Net-lineNet;
        var nominalVat=decimal.Round(act.Ks3Net*act.VatRate/100m,2,MidpointRounding.AwayFromZero);
        var vatDelta=act.Vat-nominalVat;
        var grossDelta=act.Total-(act.Ks3Net+act.Vat);
        ws.Cell(tot,1).Value="ИТОГО по акту:";
        ws.Cell(tot,8).FormulaA1=FormulaSum(rows)+Add(delta);
        ws.Cell(tot+1,1).Value="НДС " + act.VatRate.ToString("0.##",Invariant) + "%:";
        ws.Cell(tot+1,8).FormulaA1="ROUND(H"+tot+"*"+Num(act.VatRate)+"/100,2)"+Add(vatDelta);
        ws.Cell(tot+2,1).Value="ВСЕГО:";
        ws.Cell(tot+2,8).FormulaA1="SUM(H"+tot+":H"+(tot+1)+")"+Add(grossDelta);
        ws.Range(tot,8,tot+2,8).Style.NumberFormat.Format=MoneyFormat;
        Print(ws,Math.Max(tot+11,tot+2));
        return (tot+2,tot+1);
    }

    private static (int Total, int Vat) FillR4Act(IXLWorksheet ws, SqlServerKsPrintData.Package p,
        SqlServerActs.ActInfo act)
    {
        ws.Cell("E18").Value=act.Number;
        DateCell(ws,"F18",act.Date);
        DateCell(ws,"G18",act.PeriodFrom);
        DateCell(ws,"H18",act.PeriodTo);
        var lines=p.Lines.Where(x=>x.ActId==act.Id).ToList();
        var doc=p.Scope.Documents.Single(d=>d.Id==act.ContractId);
        var perPosition=doc.SmrMode.Equals("pos",StringComparison.OrdinalIgnoreCase);
        var items=new List<(string Name,string Number,string Code,string Unit,decimal? Qty,decimal Price,bool Work)>();
        foreach(var group in lines.GroupBy(x=>x.Section))
        {
            if(group.Key!="Без системы") items.Add((group.Key,"","","",null,0m,false));
            foreach(var line in group)
            {
                items.Add((line.Name,line.PositionNumber,line.WorkCode,line.Unit,line.Quantity,line.Price,
                    EstimateStatistics.IsWorkName(line.Name)));
                if(perPosition && line.WorkPrice!=0m)
                    items.Add(("Монтаж: "+line.Name,line.PositionNumber,line.WorkCode,line.Unit,line.Quantity,
                        line.WorkPrice,true));
            }
        }
        if(act.Smr!=0m && !perPosition)
            items.Add(("СМР:","","","",1m,act.Smr,true));
        if(act.Transport!=0m)items.Add(("Транспортные расходы","","","",1m,act.Transport,false));
        if(act.Pnr!=0m)items.Add(("Пуско-наладочные работы","","","",1m,act.Pnr,true));
        const int first=26,footer=90,last=112;
        var headStyle=Enumerable.Range(1,8).Select(c=>ws.Cell(26,c).Style).ToArray();
        var rowStyle=Enumerable.Range(1,8).Select(c=>ws.Cell(27,c).Style).ToArray();
        var delta=items.Count-(footer-first);
        if(delta>0)ws.Row(footer).InsertRowsAbove(delta);
        else if(delta<0)ws.Rows(first+items.Count,footer-1).Delete();
        var end=footer+delta;
        var materialRefs=new List<string>();
        var workRefs=new List<string>();
        for(int i=0;i<items.Count;i++)
        {
            var row=first+i; var item=items[i];
            for(var col=1;col<=8;col++)
            {
                var cell=ws.Cell(row,col);
                cell.Clear(XLClearOptions.Contents);
                cell.Style=(item.Qty is null ? headStyle:rowStyle)[col-1];
            }
            ws.Cell(row,1).Value=i+1;
            ws.Cell(row,2).Value=item.Number;
            ws.Cell(row,3).Value=item.Code;
            ws.Cell(row,4).Value=item.Name;
            ws.Cell(row,5).Value=item.Unit;
            if(item.Qty is not null)
            {
                ws.Cell(row,6).Value=(double)item.Qty.Value;
                ws.Cell(row,7).Value=(double)item.Price;
                ws.Cell(row,8).FormulaA1=act.RowsExact
                    ? "F"+row+"*G"+row
                    : "ROUND(F"+row+"*G"+row+",2)";
                (item.Work?workRefs:materialRefs).Add("H"+row);
            }
            ws.Row(row).Height=Math.Min(409.5,Math.Max(18.75,Math.Ceiling(item.Name.Length/60d)*14.1+4));
        }
        // Formula subtotals retain the customer's layout but reconcile to the
        // authoritative full act totals (including special rounding modes).
        var expectedWork=act.Smr+act.Pnr;
        var expectedMaterials=act.Materials+act.Transport;
        var summedWork=items.Where(x=>x.Work && x.Qty.HasValue)
            .Sum(x=>act.RowsExact ? x.Qty!.Value*x.Price
                : decimal.Round(x.Qty!.Value*x.Price,2,MidpointRounding.AwayFromZero));
        var summedMaterials=items.Where(x=>!x.Work && x.Qty.HasValue)
            .Sum(x=>act.RowsExact ? x.Qty!.Value*x.Price
                : decimal.Round(x.Qty!.Value*x.Price,2,MidpointRounding.AwayFromZero));
        ws.Cell(end,8).FormulaA1=FormulaSum(workRefs)+Add(expectedWork-summedWork);
        ws.Cell(end+1,8).FormulaA1=FormulaSum(materialRefs)+Add(expectedMaterials-summedMaterials);
        var net=expectedWork+expectedMaterials;
        var vatNominal=act.VatOnTop
            ? decimal.Round(net*act.VatRate/100m,2,MidpointRounding.AwayFromZero)
            : decimal.Round(act.Total*act.VatRate/(100m+act.VatRate),2,MidpointRounding.AwayFromZero);
        ws.Cell(end+3,8).FormulaA1=(act.VatOnTop
            ? "ROUND(SUM(H"+end+":H"+(end+1)+")*"+Num(act.VatRate)+"/100,2)"
            : "ROUND(H"+(end+2)+"*"+Num(act.VatRate)+"/(100+"+Num(act.VatRate)+"),2)")
            +Add(act.Vat-vatNominal);
        ws.Cell(end+2,8).FormulaA1="ROUND(SUM(H"+end+":H"+(end+1)+")"+
            (act.VatOnTop?"+H"+(end+3):"")+Add(act.Total-net-(act.VatOnTop?act.Vat:0m))+",2)";
        ws.Range(end,8,end+3,8).Style.NumberFormat.Format=MoneyFormat;
        ws.Cell(end+3,6).Value=(act.VatOnTop ? "НДС " : "в т.ч. НДС ") +
            act.VatRate.ToString("0.##",Invariant)+"%";
        Print(ws,last+delta);
        return (end+2,end+3);
    }

    private static void FillDnsSummary(IXLWorksheet ws, SqlServerKsPrintData.Package p,
        List<(string Name,int Total,int Vat)> refs)
    {
        var g=p.Report;
        FillAgreement(ws,p,g.Acts[0].ContractId,"A7","A9","A10","G13");
        ws.Cell("C21").Value=g.Key;
        DateCell(ws,"D21",g.Date);
        DateCell(ws,"F21",g.PeriodFrom);
        DateCell(ws,"G21",g.PeriodTo);
        if(!string.IsNullOrWhiteSpace(p.Requisites.WorkTitle))
            ws.Cell("B29").Value=p.Requisites.WorkTitle;
        ws.Cell("G30").FormulaA1=FormulaSum(refs.Select(x=>SheetRef(x.Name)+"H"+(x.Total-2)));
        ws.Cell("G31").FormulaA1=FormulaSum(refs.Select(x=>SheetRef(x.Name)+"H"+x.Vat));
        ws.Cell("G32").FormulaA1=FormulaSum(refs.Select(x=>SheetRef(x.Name)+"H"+x.Total));
        ws.Cell("G29").FormulaA1="G30";
        ws.Cell("D29").Value=(double)g.SinceStartNet;
        ws.Cell("F29").Value=(double)g.YearNet;
        for(int i=0;i<g.Previous.Count;i++)
        {
            var prior=g.Previous[i];
            var col=10+i;
            ws.Cell(26,col).Value="КС-2 №"+prior.Key;
            ws.Cell(28,col).Value=(double)prior.Net;
            ws.Cell(29,col).Value=(double)prior.Net;
            ws.Cell(30,col).Value=(double)prior.Net;
            ws.Cell(31,col).Value=(double)prior.Vat;
            ws.Cell(32,col).Value=(double)prior.Gross;
        }
        Print(ws,ws.LastRowUsed()?.RowNumber()??41,"I");
    }

    private static void FillR4Summary(IXLWorksheet ws, SqlServerKsPrintData.Package p,
        List<(string Name,int Total,int Vat)> refs)
    {
        var g=p.Report;
        ws.Cell("C18").Value=g.Key;
        DateCell(ws,"D18",g.Date);
        DateCell(ws,"F18",g.PeriodFrom);
        DateCell(ws,"G18",g.PeriodTo);
        ws.Cell("G28").FormulaA1=string.Join("+",refs.Select(x=>SheetRef(x.Name)+"H"+x.Vat));
        ws.Cell("G29").FormulaA1=string.Join("+",refs.Select(x=>SheetRef(x.Name)+"H"+x.Total));
        ws.Cell("D26").Value=(double)g.SinceStartNet;
        ws.Cell("F26").Value=(double)g.YearNet;
        ws.Cell("G31").FormulaA1="ROUND(G29*0.05,2)";
        ws.Cell("J35").Value=0.67;
        ws.Cell("J36").FormulaA1="ROUND(G29*J35,2)";
        for(int i=0;i<g.Previous.Count;i++)
        {
            var prior=g.Previous[i];
            var col=15+i;
            ws.Cell(23,col).Value="вып "+prior.Key;
            ws.Cell(25,col).Value=(double)prior.Net;
            ws.Cell(26,col).Value=(double)prior.Vat;
            ws.Cell(27,col).FormulaA1="SUM("+ws.Cell(25,col).Address.ToString()+":"+ws.Cell(26,col).Address.ToString()+")";
        }
        Print(ws,41,"I");
    }
}
