using System.Globalization;
using ClosedXML.Excel;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Python export_ks.py-compatible conventional KS-2 and one grouped KS-3.
/// Printable grids follow the original 8-column KS-2 / 9-column KS-3
/// layout, including Excel formulas and hidden-to-print cumulative columns.
/// Input is a read-only SQL Server print snapshot.
/// </summary>
public static class KsExcelForms
{
    private const string AmountFormat = "#,##0.00";
    private const string QtyFormat = "#,##0.###";
    private const string DateFormat = "dd.mm.yyyy";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly XLColor Header = XLColor.FromHtml("#E9F0FA");
    private static readonly XLColor Black = XLColor.Black;

    private sealed record ActCells(string Sheet, int TotalRow, int VatRow);

    public static void Fill(XLWorkbook book, SqlServerKsPrintData.Package package) =>
        Fill(book, package, new HashSet<string>(book.Worksheets.Select(w => w.Name), StringComparer.OrdinalIgnoreCase));

    internal static void Fill(XLWorkbook book, SqlServerKsPrintData.Package package, HashSet<string> names)
    {
        if (package.Report.Acts.Count == 0)
            throw new InvalidOperationException("Нет актов для создания КС-2.");

        var cells = new List<ActCells>();
        foreach (var act in package.Report.Acts)
        {
            var name = UniqueName("кс-2 №" + act.Number, names);
            var sheet = book.Worksheets.Add(name);
            cells.Add(FillKs2(sheet, act, package));
        }
        var groupSheet = book.Worksheets.Add(UniqueName("кс-3 №" + package.Report.Key,names));
        FillKs3(groupSheet, package, cells);
    }

    private static string UniqueName(string name, HashSet<string> names)
    {
        var clean = new string(name.Where(c => !":\\/?*[]".Contains(c)).ToArray());
        if (string.IsNullOrWhiteSpace(clean)) clean = "КС";
        clean = clean[..Math.Min(clean.Length, 31)];
        if (names.Add(clean)) return clean;
        for (var i=2;i<1000;i++)
        {
            var suffix = "_" + i.ToString(Invariant);
            var candidate = clean[..Math.Min(clean.Length,31-suffix.Length)] + suffix;
            if(names.Add(candidate)) return candidate;
        }
        throw new InvalidOperationException("Не удалось выбрать уникальное имя листа.");
    }

    private static void Start(IXLWorksheet ws, params double[] widths)
    {
        ws.Style.Font.FontName = "Arial";
        ws.Style.Font.FontSize = 9;
        ws.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Style.Alignment.WrapText = true;
        for(var i=0;i<widths.Length;i++)ws.Column(i+1).Width=widths[i];
        ws.ShowGridLines = false;
    }

    private static void Text(IXLWorksheet ws,string address,string value,
        bool bold=false,int fontSize=9)
    {
        var cell=ws.Cell(address);
        cell.Value=value;
        cell.Style.Font.Bold=bold;
        cell.Style.Font.FontSize=fontSize;
    }

    private static void Merged(IXLWorksheet ws,int row,int first,int last,
        string value,bool bold=false)
    {
        ws.Range(row,first,row,last).Merge();
        var cell=ws.Cell(row,first);
        cell.Value=value;
        cell.Style.Font.Bold=bold;
    }

    private static void Date(IXLWorksheet ws,string address,DateTime? day)
    {
        if (day is null) return;
        ws.Cell(address).Value=day.Value;
        ws.Cell(address).Style.NumberFormat.Format=DateFormat;
        ws.Cell(address).Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Center;
    }

    private static void Borders(IXLWorksheet ws,int firstRow,int lastRow,int columns)
    {
        var r=ws.Range(firstRow,1,lastRow,columns);
        r.Style.Border.OutsideBorder=XLBorderStyleValues.Thin;
        r.Style.Border.InsideBorder=XLBorderStyleValues.Thin;
        r.Style.Border.OutsideBorderColor=Black;
        r.Style.Border.InsideBorderColor=Black;
    }

    private static void Title(IXLWorksheet ws,int row,int last,string title)
    {
        Merged(ws,row,1,last,title,true);
        ws.Cell(row,1).Style.Font.FontSize=12;
        ws.Cell(row,1).Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Center;
        ws.Row(row).Height=23;
    }

    private static void Print(IXLWorksheet ws,int finalRow,int lastColumn,int headerRow)
    {
        ws.PageSetup.PageOrientation=XLPageOrientation.Landscape;
        ws.PageSetup.PaperSize=XLPaperSize.A4Paper;
        ws.PageSetup.Margins.Left=0.35;
        ws.PageSetup.Margins.Right=0.35;
        ws.PageSetup.Margins.Top=0.50;
        ws.PageSetup.Margins.Bottom=0.50;
        ws.PageSetup.FitToPages(1,0);
        ws.PageSetup.PrintAreas.Clear();
        ws.PageSetup.PrintAreas.Add("A1:"+((char)('A'+lastColumn-1))+finalRow);
        // FreezeRows does not repeat headings in print and freezes most of the visible sheet.
        // The standard KS-2 repeats its three table heading rows on subsequent pages.
        if(headerRow==23)ws.PageSetup.SetRowsToRepeatAtTop(21,23);
    }

    private static string DecimalText(decimal value) =>
        value.ToString("0.########",Invariant);
    private static string Delta(decimal value) =>
        value==0m ? "" : (value>0m ? "+" : "")+DecimalText(value);
    private static string Sum(IEnumerable<string> references)
    {
        var items=references.ToArray();
        return items.Length==0 ? "0" : "SUM("+string.Join(",",items)+")";
    }
    private static string SheetReference(string name) =>
        "'" + name.Replace("'","''") + "'!";

    private static string Customer(SqlServerKsRequisites.Values r) =>
        string.IsNullOrWhiteSpace(r.CustomerFull) ? r.CustomerOrganization : r.CustomerFull;
    private static string Site(SqlServerKsRequisites.Values r) =>
        string.IsNullOrWhiteSpace(r.SiteFull) ? r.ObjectName : r.SiteFull;
    private static string Contractor(SqlServerKsPrintData.Contractor co) =>
        string.IsNullOrWhiteSpace(co.LegalName) ? co.CompanyName : co.LegalName;

    private static ActCells FillKs2(IXLWorksheet ws,SqlServerActs.ActInfo act,
        SqlServerKsPrintData.Package p)
    {
        Start(ws,6.4,8,60,11,10,11,13,15);
        var contract=p.Scope.Documents.Single(x=>x.Id==act.ContractId);
        var co=p.Contractors[act.ContractId];
        var r=p.Requisites;
        for(var headingRow=1;headingRow<=3;headingRow++)
        {
            ws.Range(headingRow,5,headingRow,8).Merge();
            ws.Row(headingRow).Height=18;
        }
        Text(ws,"E1","Унифицированная форма № КС-2");
        Text(ws,"E2","Утверждена постановлением Госкомстата России");
        Text(ws,"E3","от 11 ноября 1999 года №100");
        ws.Range("E1:H3").Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Right;
        ws.Range("E1:H3").Style.Font.FontSize=8;
        Text(ws,"G4","Код");
        Text(ws,"C5","Форма по ОКУД");
        Text(ws,"G5","0322005");
        Merged(ws,6,1,4,"Заказчик (Генподрядчик) - "+Customer(r));
        Merged(ws,7,1,4,"Подрядчик (Субподрядчик) - "+Contractor(co));
        Merged(ws,8,1,4,"Стройка - "+Site(r));
        ws.Row(6).Height=39;
        ws.Row(7).Height=45;
        ws.Row(8).Height=52;
        Text(ws,"C11","Договор подряда");
        Text(ws,"E11","номер");
        ws.Range("F11:H11").Merge();
        Text(ws,"F11",p.Scope.AgreementNumber);
        ws.Cell("F11").Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Center;
        Text(ws,"E12","дата");
        Text(ws,"F12",co.ContractDate);
        Text(ws,"E13","Вид операции");
        Text(ws,"F13","СМР");
        Merged(ws,14,1,8,"Источник: "+contract.Label+
            (string.IsNullOrWhiteSpace(contract.SitePart) ? "" : "; "+contract.SitePart));
        Text(ws,"E15","Номер документа");
        Text(ws,"F15","Дата составления");
        ws.Range("G15:H15").Merge();
        Text(ws,"G15","Отчётный период");
        Text(ws,"G16","с");
        Text(ws,"H16","по");
        ws.Range("E15:H17").Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Center;
        ws.Row(15).Height=39;
        ws.Row(16).Height=20;
        ws.Row(17).Height=22;
        Text(ws,"E17","№"+act.Number,true);
        Date(ws,"F17",act.Date);
        Date(ws,"G17",act.PeriodFrom);
        Date(ws,"H17",act.PeriodTo);
        Title(ws,18,8,"АКТ");
        Title(ws,19,8,"О ПРИЕМКЕ ВЫПОЛНЕННЫХ РАБОТ");
        ws.Range("A21:B21").Merge();
        Text(ws,"A21","Номер",true);
        ws.Range("C21:C22").Merge(); Text(ws,"C21","Наименование работ",true);
        ws.Range("D21:D22").Merge(); Text(ws,"D21",
            contract.Form=="r4" ? "Код работ" : "Номер единичной расценки",true);
        ws.Range("E21:E22").Merge(); Text(ws,"E21","Единица измерения",true);
        ws.Range("F21:H21").Merge(); Text(ws,"F21","Выполнено работ",true);
        Text(ws,"A22","по порядку",true);
        Text(ws,"B22","поз. по смете",true);
        Text(ws,"F22","количество",true);
        Text(ws,"G22","цена за единицу, руб.",true);
        Text(ws,"H22","стоимость, руб.",true);
        for(var i=1;i<=8;i++)ws.Cell(23,i).Value=i;
        var titleRows=ws.Range(21,1,23,8);
        titleRows.Style.Fill.BackgroundColor=Header;
        titleRows.Style.Font.Bold=true;
        titleRows.Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Center;
        Borders(ws,21,23,8);
        ws.Row(21).Height=34;
        ws.Row(22).Height=44;
        ws.Row(23).Height=20;

        var sections=p.SectionsFor(act);
        var perPosition=contract.SmrMode.Equals("pos",StringComparison.OrdinalIgnoreCase);
        var materialRefs=new List<string>();
        var workRefs=new List<string>();
        var transportRefs=new List<string>();
        decimal materialInput=0m,workInput=0m,transportInput=0m;
        var row=24; var sequence=0;
        foreach(var group in sections)
        {
            if(!string.IsNullOrWhiteSpace(group.Name) && group.Name!="Без системы")
            {
                ws.Cell(row,1).Value=++sequence;
                ws.Cell(row,3).Value=group.Name;
                ws.Range(row,1,row,8).Style.Font.Bold=true;
                ws.Range(row,1,row,8).Style.Fill.BackgroundColor=Header;
                Borders(ws,row,row,8);
                ws.Row(row).Height=22;
                row++;
            }
            foreach(var line in group.Lines)
            {
                WritePosition(ws,row,++sequence,line.Name,line.PositionNumber,
                    contract.Form=="r4" ? line.WorkCode : "",line.Unit,line.Quantity,line.Price,false,
                    rounding:true);
                materialRefs.Add("H"+row);
                materialInput+=decimal.Round(line.Quantity*line.Price,2,MidpointRounding.AwayFromZero);
                row++;
                if(perPosition && line.WorkPrice!=0m)
                {
                    WritePosition(ws,row,++sequence,"Монтаж: "+line.Name,"",
                        "",line.Unit,line.Quantity,line.WorkPrice,true,rounding:true);
                    workRefs.Add("H"+row);
                    workInput+=decimal.Round(line.Quantity*line.WorkPrice,2,MidpointRounding.AwayFromZero);
                    row++;
                }
            }
            void SectionAmount(string caption,decimal amount,List<string> refs)
            {
                if(amount==0m)return;
                ws.Cell(row,1).Value=++sequence;
                ws.Cell(row,3).Value=caption;
                ws.Cell(row,8).Value=(double)amount;
                ws.Cell(row,8).Style.NumberFormat.Format=AmountFormat;
                ws.Range(row,1,row,8).Style.Font.Bold=true;
                Borders(ws,row,row,8);
                ws.Row(row).Height=22;
                refs.Add("H"+row++);
            }
            SectionAmount("ТРАНСПОРТНЫЕ РАСХОДЫ:",group.Transport,transportRefs);
            transportInput+=group.Transport;
            SectionAmount("СМР:",group.Smr,workRefs);
            workInput+=group.Smr;
        }

        var parts=new List<string>();
        var matRow=TotalLine(ws,ref row,"ИТОГО СТОИМОСТЬ ОБОРУДОВАНИЯ:",
            Sum(materialRefs)+Delta(act.Materials-materialInput));
        parts.Add("H"+matRow);
        if (act.Smr!=0m || workRefs.Count>0)
        {
            var work=workRefs.Count==0 ? DecimalText(act.Smr) :
                Sum(workRefs)+Delta(act.Smr-workInput);
            parts.Add("H"+TotalLine(ws,ref row,"СТОИМОСТЬ РАБОТ:",work));
        }
        if(act.Transport!=0m||transportRefs.Count>0)
            parts.Add("H"+TotalLine(ws,ref row,transportRefs.Count>0?"ИТОГО ТРАНСПОРТНЫЕ РАСХОДЫ:":"ТРАНСПОРТНЫЕ РАСХОДЫ:",
                transportRefs.Count>0?Sum(transportRefs)+Delta(act.Transport-transportInput):DecimalText(act.Transport)));
        if(act.Pnr!=0m)
            parts.Add("H"+TotalLine(ws,ref row,"ПУСКО-НАЛАДОЧНЫЕ РАБОТЫ:",DecimalText(act.Pnr)));

        var baseAmount=act.Materials+act.Smr+act.Transport+act.Pnr;
        int grossRow,vatRow;
        var rate=DecimalText(act.VatRate);
        if(act.VatOnTop)
        {
            var net=TotalLine(ws,ref row,"ИТОГО без НДС: ",
                Sum(parts)+Delta(act.Ks3Net-baseAmount));
            vatRow=TotalLine(ws,ref row,"НДС "+rate+"%:",
                "ROUND(H"+net+"*"+rate+"/100,2)"+
                Delta(act.Vat-decimal.Round(act.Ks3Net*act.VatRate/100m,2,MidpointRounding.AwayFromZero)),false);
            grossRow=TotalLine(ws,ref row,"ВСЕГО с НДС "+rate+"%:",
                "SUM(H"+net+":H"+vatRow+")"+
                Delta(act.Total-act.Ks3Net-act.Vat));
        }
        else
        {
            var adjusted=act.ManualTotalCents>=0||act.DiscountPercent!=0m;
            grossRow=TotalLine(ws,ref row,"ВСЕГО с НДС "+rate+"%:",
                Sum(parts)+Delta((adjusted?decimal.Round(baseAmount,2,MidpointRounding.ToEven):act.Total)-baseAmount));
            if(act.ManualTotalCents>=0)
            {
                var target=(act.ManualTotalCents/100m).ToString("0.00",Invariant);
                var adjustment=TotalLine(ws,ref row,"Корректировка до согласованной суммы:",
                    "ROUND("+target+"-ROUND(H"+grossRow+",2),2)",false);
                grossRow=TotalLine(ws,ref row,"ВСЕГО С УЧЁТОМ КОРРЕКТИРОВКИ:",
                    "ROUND(ROUND(H"+grossRow+",2)+H"+adjustment+",2)");
            }
            else if(act.DiscountPercent!=0m)
            {
                var pct=act.DiscountPercent.ToString("0.################",Invariant);
                var discount=TotalLine(ws,ref row,"Скидка "+pct+"%:",
                    "-ROUND(ROUND(H"+grossRow+",2)*"+pct+"/100,2)",false);
                grossRow=TotalLine(ws,ref row,"ВСЕГО С УЧЁТОМ СКИДКИ:",
                    "ROUND(H"+grossRow+"+H"+discount+",2)");
            }
            var nominal=act.VatRate==0m ? 0m :
                decimal.Round(act.Total*act.VatRate/(100m+act.VatRate),2,MidpointRounding.AwayFromZero);
            vatRow=TotalLine(ws,ref row,"в том числе НДС "+rate+"%:",
                "ROUND(H"+grossRow+"*"+rate+"/(100+"+rate+"),2)"+Delta(act.Vat-nominal),false);
        }

        row+=2;
        Signature(ws,row,"Сдал:",co.SignerPosition,co.CompanyName,co.SignerName);
        row+=4;
        Signature(ws,row,"Принял:",r.CustomerPosition,r.CustomerOrganization,r.CustomerName);
        if(!string.IsNullOrWhiteSpace(r.SecondRole))
        {
            row+=4;
            Signature(ws,row,r.SecondRole+":",r.SecondPosition,r.SecondOrganization,r.SecondName);
        }
        Print(ws,row+3,8,23);
        return new ActCells(ws.Name,grossRow,vatRow);
    }

    private static void WritePosition(IXLWorksheet ws,int row,int serial,string description,
        string position,string code,string unit,decimal qty,decimal price,bool italic,bool rounding)
    {
        ws.Cell(row,1).Value=serial;
        ws.Cell(row,2).Value=position;
        ws.Cell(row,3).Value=description;
        ws.Cell(row,4).Value=code;
        ws.Cell(row,5).Value=unit;
        ws.Cell(row,6).Value=(double)qty;
        ws.Cell(row,7).Value=(double)price;
        ws.Cell(row,8).FormulaA1=(rounding?"ROUND(F"+row+"*G"+row+",2)":"F"+row+"*G"+row);
        ws.Cell(row,6).Style.NumberFormat.Format=QtyFormat;
        ws.Range(row,7,row,8).Style.NumberFormat.Format=AmountFormat;
        if(italic)ws.Cell(row,3).Style.Font.Italic=true;
        ws.Row(row).Height=Math.Min(90,Math.Max(22,Math.Ceiling(description.Length/75d)*13));
        Borders(ws,row,row,8);
    }

    private static int TotalLine(IXLWorksheet ws,ref int row,string title,string formula,bool bold=true)
    {
        ws.Range(row,3,row,7).Merge();
        ws.Cell(row,3).Value=title;
        ws.Cell(row,3).Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Right;
        ws.Cell(row,8).FormulaA1=formula;
        ws.Cell(row,8).Style.NumberFormat.Format=AmountFormat;
        ws.Range(row,1,row,8).Style.Font.Bold=bold;
        if(bold)ws.Range(row,1,row,8).Style.Fill.BackgroundColor=Header;
        Borders(ws,row,row,8);
        ws.Row(row).Height=24;
        return row++;
    }

    private static void Signature(IXLWorksheet ws,int row,string caption,
        string title,string organization,string signer)
    {
        Merged(ws,row,2,7,caption,true);
        Merged(ws,row+1,2,7,
            (title+" "+organization+"    ____________________    "+signer).Trim());
        ws.Row(row+1).Height=38;
        ws.Cell(row+1,2).Style.Alignment.WrapText=true;
        Text(ws,"C"+(row+2),"МП");
    }

    private static void FillKs3(IXLWorksheet ws,SqlServerKsPrintData.Package p,
        IReadOnlyList<ActCells> refs)
    {
        Start(ws,6.7,46,10,18,2,18,18,6,6);
        var r=p.Requisites;var report=p.Report;
        var co=p.Contractors[report.Acts[0].ContractId];
        for(var headingRow=1;headingRow<=3;headingRow++)
        {
            ws.Range(headingRow,4,headingRow,7).Merge();
            ws.Row(headingRow).Height=18;
        }
        Text(ws,"D1","Унифицированная форма № КС-3");
        Text(ws,"D2","Утверждена постановлением Госкомстата России");
        Text(ws,"D3","от 11.11.99 № 100");
        ws.Range("D1:G3").Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Right;
        ws.Range("D1:G3").Style.Font.FontSize=8;
        Text(ws,"G4","Код");Text(ws,"F5","Форма по ОКУД");Text(ws,"G5","0322001");
        Text(ws,"A6","Инвестор:");Text(ws,"F6","по ОКПО");
        Merged(ws,7,1,5,"Заказчик (Генподрядчик) - "+Customer(r));
        Merged(ws,8,1,5,"Подрядчик (Субподрядчик) - "+Contractor(co));
        Merged(ws,9,1,5,"Стройка - "+Site(r));
        ws.Row(7).Height=40;
        ws.Row(8).Height=46;
        ws.Row(9).Height=53;
        Text(ws,"A12","Договор подряда (контракт)");
        Text(ws,"F12","номер");
        ws.Range("G12:I12").Merge();
        Text(ws,"G12",report.AgreementNumber);
        ws.Cell("G12").Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Center;
        Text(ws,"F13","дата");
        Text(ws,"G13",co.ContractDate);
        Text(ws,"F14","Вид операции");
        ws.Range("F16:G16").Merge();
        Text(ws,"C16","Номер");Text(ws,"D16","Дата");
        Text(ws,"F16","Отчётный период");
        Text(ws,"C17","документа");Text(ws,"D17","составления");
        Text(ws,"F17","с");Text(ws,"G17","по");
        Text(ws,"B18","СПРАВКА",true,12);
        ws.Cell("B18").Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Center;
        ws.Range("C16:G18").Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Center;
        ws.Row(16).Height=25;
        ws.Row(17).Height=25;
        ws.Row(18).Height=27;
        Text(ws,"C18",report.Key,true);
        Date(ws,"D18",report.Date);Date(ws,"F18",report.PeriodFrom);Date(ws,"G18",report.PeriodTo);
        Title(ws,20,7,"О СТОИМОСТИ ВЫПОЛНЕННЫХ РАБОТ И ЗАТРАТ");
        ws.Range("A22:A23").Merge();
        ws.Range("B22:B23").Merge();
        ws.Range("C22:C23").Merge();
        ws.Range("D22:G22").Merge();
        ws.Range("D23:E23").Merge();
        Text(ws,"A22","Номер п/п",true);
        Text(ws,"B22","Наименование пусковых комплексов, этапов, объектов, видов выполненных работ, оборудования, затрат",true);
        Text(ws,"C22","Код",true);
        Text(ws,"D22","Стоимость выполненных работ и затрат, руб.",true);
        Text(ws,"D23","с начала проведения работ",true);
        Text(ws,"F23","с начала года",true);
        Text(ws,"G23","в том числе за отчётный период",true);
        for(var i=0;i<6;i++)
            ws.Cell(24,new[]{1,2,3,4,6,7}[i]).Value=i+1;
        ws.Range(22,1,24,7).Style.Fill.BackgroundColor=Header;
        ws.Range(22,1,24,7).Style.Font.Bold=true;
        ws.Range(22,1,24,7).Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Center;
        ws.Row(22).Height=49;
        ws.Row(23).Height=41;
        ws.Row(24).Height=21;
        Borders(ws,22,29,7);
        ws.Cell("A25").Value=1;
        Text(ws,"B25","Всего работ и затрат, включаемых в стоимость работ, в том числе:");
        ws.Cell("A26").Value=2;
        Text(ws,"B26",string.IsNullOrWhiteSpace(r.WorkTitle) ? "Выполненные работы" : r.WorkTitle);
        ws.Row(25).Height=43;
        ws.Row(26).Height=30;
        for(var totalRow=27;totalRow<=29;totalRow++)ws.Row(totalRow).Height=24;
        foreach(var row in new[]{25,26})
        {
            ws.Range(row,4,row,5).Merge();
            ws.Cell(row,4).Value=(double)report.SinceStartNet;
            ws.Cell(row,6).Value=(double)report.YearNet;
            ws.Cell(row,7).FormulaA1="G27";
        }
        Text(ws,"A27","Итого",true);
        ws.Cell("G27").FormulaA1="G29-G28";
        Text(ws,"A28","Сумма НДС",true);
        ws.Cell("G28").FormulaA1=Sum(refs.Select(x=>SheetReference(x.Sheet)+"H"+x.VatRow));
        Text(ws,"A29","Всего",true);
        ws.Cell("G29").FormulaA1=Sum(refs.Select(x=>SheetReference(x.Sheet)+"H"+x.TotalRow));
        ws.Range(25,4,29,7).Style.NumberFormat.Format=AmountFormat;
        ws.Range(27,1,29,7).Style.Font.Bold=true;
        var prev=report.Previous;
        for(var i=0;i<prev.Count;i++)
        {
            var prior=prev[i];
            var col=10+i;
            ws.Column(col).Width=14;
            ws.Cell(23,col).Value="КС-3 №"+prior.Key;
            foreach(var (row,amount) in new[]{
                (25,prior.Net),(26,prior.Net),(27,prior.Net),
                (28,prior.Vat),(29,prior.Gross)})
            {
                ws.Cell(row,col).Value=(double)amount;
                ws.Cell(row,col).Style.NumberFormat.Format=AmountFormat;
            }
            BordersSingle(ws,col,23,29);
        }
        var sign=31;
        Signature(ws,sign,"Принял:",r.CustomerPosition,r.CustomerOrganization,r.CustomerName);
        if(!string.IsNullOrWhiteSpace(r.SecondRole))
        {
            sign+=4;
            Signature(ws,sign,r.SecondRole+":",r.SecondPosition,r.SecondOrganization,r.SecondName);
        }
        sign+=4;
        Signature(ws,sign,"Сдал:",co.SignerPosition,co.CompanyName,co.SignerName);
        Print(ws,sign+2,7,24); // Only A:G belongs on the printed KS-3; H+ are excluded.
    }

    private static void BordersSingle(IXLWorksheet ws,int column,int start,int end)
    {
        var r=ws.Range(start,column,end,column);
        r.Style.Border.OutsideBorder=XLBorderStyleValues.Thin;
        r.Style.Border.InsideBorder=XLBorderStyleValues.Thin;
        r.Style.Border.OutsideBorderColor=Black;
        r.Style.Border.InsideBorderColor=Black;
    }
}
