using System.Globalization;
using ClosedXML.Excel;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>
/// Read-only act VOR export using the exact Python XLSX templates.
/// Styling/merges/signature appendix originate in the archived workbooks,
/// while all quantities and headers come from the SQL Server print snapshot.
/// </summary>
public static class KsVorForms
{
    private sealed record Row(string? Heading, SqlServerKsPrintData.Line? Line);
    private const string WholeQuantityFormat="#,##0";
    private const string FractionalQuantityFormat="#,##0.####";

    public static XLWorkbook Create(SqlServerKsPrintData.Package package, long actId)
    {
        var act=package.Report.Acts.Single(a=>a.Id==actId);
        var form=KsCustomerForms.Resolve(package);
        var name=form switch
        {
            KsCustomerForms.Form.Dns=>"vor_dns_template.xlsx",
            KsCustomerForms.Form.R4=>"vor_r4_template.xlsx",
            KsCustomerForms.Form.Dneprovsky=>"vor_template.xlsx",
            _=>"vor_template.xlsx"
        };
        var wb=KsCustomerForms.Load(name);
        try
        {
            var ws=form==KsCustomerForms.Form.R4 ? wb.Worksheet("ВОР") : wb.Worksheet(1);
            var groups=package.Lines.Where(x=>x.ActId==actId && x.Quantity>0m)
                .GroupBy(x=>x.Section).ToList();
            var data=new List<Row>();
            foreach(var group in groups)
            {
                if(group.Key!="Без системы")
                    data.Add(new Row(group.Key,null));
                data.AddRange(group.Select(x=>new Row(null,x)));
            }
            switch(form)
            {
                case KsCustomerForms.Form.Dns:
                    FillDns(ws,package,act,data);
                    break;
                case KsCustomerForms.Form.R4:
                    FillR4(ws,act,data);
                    break;
                default:
                    FillStandard(ws,package,act,data);
                    break;
            }
            return wb;
        }
        catch { wb.Dispose(); throw; }
    }

    private static void ResizeBody(IXLWorksheet ws,int first,int footer,int count)
    {
        var delta=count-(footer-first);
        if(delta>0) ws.Row(footer).InsertRowsAbove(delta);
        else if(delta<0) ws.Rows(first+count,footer-1).Delete();
    }

    private static void Print(IXLWorksheet ws,int end,string last)
    {
        ws.PageSetup.PrintAreas.Clear();
        ws.PageSetup.PrintAreas.Add("A1:"+last+end);
        ws.PageSetup.FitToPages(1,0);
        ws.ShowGridLines=false;
    }

    private static void Style(IXLWorksheet ws,int row,IXLStyle[] styles)
    {
        for(int col=1;col<=styles.Length;col++)
        {
            ws.Cell(row,col).Clear(XLClearOptions.Contents);
            ws.Cell(row,col).Style=styles[col-1];
        }
    }

    private static IXLStyle[] Snapshot(IXLWorksheet ws,int row,int cols) =>
        Enumerable.Range(1,cols).Select(col=>ws.Cell(row,col).Style).ToArray();

    private static void SetQuantity(IXLWorksheet ws,int row,int col,decimal qty)
    {
        var rounded=decimal.Round(qty,4,MidpointRounding.AwayFromZero);
        var cell=ws.Cell(row,col);
        cell.Value=(double)rounded;
        // Excel retains the decimal separator in "0.####" even for whole values.
        // Decide from the exported rounded number, not the original quantity.
        cell.Style.NumberFormat.Format=rounded==decimal.Truncate(rounded)
            ?WholeQuantityFormat:FractionalQuantityFormat;
    }

    private static void FillStandard(IXLWorksheet ws,SqlServerKsPrintData.Package p,
        SqlServerActs.ActInfo act,List<Row> data)
    {
        const int first=10,footer=16,last=24,cols=5;
        var style=Snapshot(ws,13,cols);
        var originalEnd=ws.LastRowUsed()?.RowNumber()??last;
        ws.Cell("A1").Value="Ведомость объемов работ №"+act.Number;
        ws.Cell("E3").Value=act.Date.ToString("dd.MM.yyyy'г.'",CultureInfo.GetCultureInfo("ru-RU"));
        var site=string.IsNullOrWhiteSpace(p.Requisites.SiteFull)
            ?p.Requisites.ObjectName:p.Requisites.SiteFull;
        ws.Cell("A5").Value="Объект: "+site;
        ResizeBody(ws,first,footer,data.Count);
        for(int i=0;i<data.Count;i++)
        {
            int row=first+i;Style(ws,row,style);
            var item=data[i];
            if(item.Heading is { } heading)
            {
                ws.Cell(row,2).Value=heading;
                ws.Range(row,1,row,cols).Style.Font.Bold=true;
                ws.Row(row).Height=Math.Max(15,Math.Ceiling(heading.Length/50d)*13.2+3);
            }
            else if(item.Line is { } line)
            {
                ws.Cell(row,1).Value=data.Take(i+1).Count(x=>x.Line is not null);
                ws.Cell(row,2).Value=line.Name;
                ws.Cell(row,3).Value=line.Unit;
                SetQuantity(ws,row,4,line.Quantity);
                ws.Row(row).Height=Math.Max(15,Math.Ceiling(line.Name.Length/50d)*13.2+3);
            }
        }
        Print(ws,originalEnd+data.Count-(footer-first),"E");
    }

    private static void FillDns(IXLWorksheet ws,SqlServerKsPrintData.Package p,
        SqlServerActs.ActInfo act,List<Row> data)
    {
        const int first=3,footer=26,last=38,cols=6;
        var section=Snapshot(ws,3,cols);
        var position=Snapshot(ws,6,cols);
        ws.Cell("A1").Value="Ведомость выполненных работ №"+act.Number+
            " от "+act.Date.ToString("dd.MM.yyyy'г.'",CultureInfo.GetCultureInfo("ru-RU"));
        ResizeBody(ws,first,footer,data.Count);
        int serial=0,sectionNumber=0;
        for(int i=0;i<data.Count;i++)
        {
            int row=first+i;
            var item=data[i];
            Style(ws,row,item.Heading is null?position:section);
            ws.Cell(row,1).Value=++serial;
            if(item.Heading is { } heading)
            {
                ws.Cell(row,4).Value="Раздел "+(++sectionNumber)+". "+heading;
                ws.Row(row).Height=Math.Max(15,Math.Ceiling(heading.Length/80d)*12+3);
            }
            else if(item.Line is { } line)
            {
                ws.Cell(row,2).Value=line.PositionNumber;
                ws.Cell(row,3).Value=line.Basis;
                ws.Cell(row,4).Value=line.Name;
                ws.Cell(row,5).Value=line.Unit;
                SetQuantity(ws,row,6,line.Quantity);
                ws.Row(row).Height=Math.Max(15,Math.Ceiling(line.Name.Length/80d)*12+3);
            }
        }
        var end=last+data.Count-(footer-first);
        var systemNames=string.Join(", ",data.Where(x=>x.Heading is not null)
            .Select(x=>x.Heading).Distinct());
        for(int row=first+data.Count;row<=end;row++)
        {
            var cell=ws.Cell(row,1);
            var value=cell.GetString();
            if(value.StartsWith("1. Исполнительная схема",StringComparison.Ordinal))
                cell.Value="1. Исполнительная схема на монтаж систем вентиляции "+
                    systemNames+" (на __ листах)";
        }
        Print(ws,end,"F");
    }

    private static void FillR4(IXLWorksheet ws,SqlServerActs.ActInfo act,List<Row> data)
    {
        const int first=7,footer=768,last=792,cols=11;
        var heading=Snapshot(ws,7,cols);
        var position=Snapshot(ws,8,cols);
        ws.Cell("A2").Value="Ведомость объемов работ №"+act.Number;
        ResizeBody(ws,first,footer,data.Count);
        int serial=0;
        for(int i=0;i<data.Count;i++)
        {
            int row=first+i;
            var item=data[i];
            Style(ws,row,item.Heading is null?position:heading);
            if(item.Heading is { } section)
            {
                ws.Cell(row,6).Value=section;
                ws.Row(row).Height=Math.Max(15,Math.Ceiling(section.Length/65d)*14.1+3);
            }
            else if(item.Line is { } line)
            {
                ws.Cell(row,1).Value=++serial;
                ws.Cell(row,3).Value=line.OutsideKp?"":line.PositionNumber;
                ws.Cell(row,4).Value=line.WorkCode;
                ws.Cell(row,6).Value=line.Name;
                ws.Cell(row,7).Value=line.Unit;
                SetQuantity(ws,row,8,line.Quantity);
                ws.Row(row).Height=Math.Max(15,Math.Ceiling(line.Name.Length/65d)*14.1+3);
            }
        }
        Print(ws,last+data.Count-(footer-first),"K");
    }
}
