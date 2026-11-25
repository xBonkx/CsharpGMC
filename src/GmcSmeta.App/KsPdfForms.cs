using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>
/// Pure SQL Server snapshot -> printable PDF, without Word/Excel/Qt dependency.
/// Python-style classic black grid, repeatable page headers and Cyrillic fonts.
/// This printable approximation is distinct from customer-specific XLSX templates.
/// </summary>
public static class KsPdfForms
{
    private const string MoneyFormat="#,##0.00";
    private static readonly CultureInfo Ru=CultureInfo.GetCultureInfo("ru-RU");

    public static byte[] Create(SqlServerKsPrintData.Package p)
    {
        if (p.Report.Acts.Count==0) throw new InvalidOperationException("Нет актов КС-2 для PDF.");
        var form=KsCustomerForms.Resolve(p);
        KsCustomerForms.ValidateAdjustments(p,form);
        if (form==KsCustomerForms.Form.Dneprovsky)
            throw new NotSupportedException("Днепровский: оригинальная форма заказчика отсутствует.");
        using var book=new PdfDocument();
        book.Info.Title="КС-2 + КС-3 №"+p.Report.Key;
        book.Info.Subject="ГМС Смета — ведомости из SQL Server";
        using var writer=new Canvas(book);
        foreach(var act in p.Report.Acts)
            DrawAct(writer,p,act);
        DrawSummary(writer,p);
        using var stream=new MemoryStream();
        book.Save(stream,false);
        return stream.ToArray();
    }

    public static bool Save(Window? owner,string proposedName,Func<byte[]> generate)
    {
        var dlg=new SaveFileDialog
        {
            Title="Сохранить КС в PDF",
            Filter="PDF (*.pdf)|*.pdf",
            FileName=string.Concat(proposedName.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c))
        };
        if(owner is not null && dlg.ShowDialog(owner)!=true)return false;
        if(owner is null && dlg.ShowDialog()!=true)return false;
        try
        {
            var pdf=generate();
            if(pdf.Length<10 || pdf[0]!='%' || pdf[1]!='P' || pdf[2]!='D' || pdf[3]!='F')
                throw new InvalidDataException("PDF не сформирован.");
            File.WriteAllBytes(dlg.FileName,pdf);
        }
        catch(Exception error)
        {
            MessageBox.Show(owner,error.Message,"Экспорт PDF",
                MessageBoxButton.OK,MessageBoxImage.Warning);
            return false;
        }
        try { Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute=true }); }
        catch { /* Saved PDF exists even when no PDF viewer is installed. */ }
        return true;
    }

    private static string Amount(decimal money) => money.ToString(MoneyFormat,Ru);
    private static string Qty(decimal quantity) => quantity.ToString("#,##0.####",Ru);
    private static string Customer(SqlServerKsRequisites.Values r) =>
        string.IsNullOrWhiteSpace(r.CustomerFull)?r.CustomerOrganization:r.CustomerFull;
    private static string Site(SqlServerKsRequisites.Values r) =>
        string.IsNullOrWhiteSpace(r.SiteFull)?r.ObjectName:r.SiteFull;
    private static string Contractor(SqlServerKsPrintData.Contractor co) =>
        string.IsNullOrWhiteSpace(co.LegalName)?co.CompanyName:co.LegalName;

    private static readonly double[] KsColumns={30,60,283,65,55,72,88,93};

    private static void DrawAct(Canvas pdf,SqlServerKsPrintData.Package p,SqlServerActs.ActInfo act)
    {
        var contractor=p.Contractors[act.ContractId];
        var r=p.Requisites;
        void StartPage(bool continued)
        {
            pdf.NewPage();
            pdf.Text("Унифицированная форма № КС-2  •  ОКУД 0322005",0,pdf.Y,750,12,true);
            pdf.Y+=17;
            pdf.Text("Заказчик: "+Customer(r),0,pdf.Y,750,16);
            pdf.Y+=16;
            pdf.Text("Подрядчик: "+Contractor(contractor),0,pdf.Y,750,16);
            pdf.Y+=16;
            pdf.Text("Стройка: "+Site(r),0,pdf.Y,750,16);
            pdf.Y+=18;
            pdf.Text("АКТ О ПРИЁМКЕ ВЫПОЛНЕННЫХ РАБОТ"+(continued?" (продолжение)":""),0,pdf.Y,750,17,true,13);
            pdf.Y+=23;
            pdf.Text("КС-2 №"+act.Number+"  от "+act.Date.ToString("dd.MM.yyyy")+
                "    Договор №"+p.Report.AgreementNumber+
                "    Период: "+act.PeriodFrom?.ToString("dd.MM.yyyy")+" — "+
                act.PeriodTo?.ToString("dd.MM.yyyy"),0,pdf.Y,750,13);
            pdf.Y+=20;
            pdf.Grid(KsColumns,new[]{"№","Поз.","Наименование работ","Код работ","Ед.","Кол-во","Цена, ₽","Сумма, ₽"},33,true);
        }
        StartPage(false);
        var index=0;
        var lineRows=p.Lines.Where(x=>x.ActId==act.Id).GroupBy(x=>x.Section);
        foreach(var group in lineRows)
        {
            if(!string.IsNullOrWhiteSpace(group.Key)&&group.Key!="Без системы")
            {
                if(pdf.Y>pdf.Bottom-33) StartPage(true);
                pdf.Grid(KsColumns,new[]{"","",group.Key,"","","","",""},24,true);
            }
            foreach(var line in group)
            {
                var height=pdf.RowHeight(line.Name,283);
                if(pdf.Y>pdf.Bottom-height-5)StartPage(true);
                pdf.Grid(KsColumns,new[]{
                    (++index).ToString(Invariant()),line.PositionNumber,line.Name,
                    line.WorkCode,line.Unit,Qty(line.Quantity),Amount(line.Price),
                    Amount(decimal.Round(line.Quantity*line.Price,2,MidpointRounding.AwayFromZero))
                },height,false);
            }
        }
        // Full authoritative act figures, not merely the sum of material lines.
        var totals=new List<(string Caption,decimal Value)>
        {
            ("Материалы",act.Materials),("Строительно-монтажные работы",act.Smr)
        };
        if(act.Transport!=0m)totals.Add(("Транспортные расходы",act.Transport));
        if(act.Pnr!=0m)totals.Add(("Пусконаладочные работы",act.Pnr));
        if(!act.VatOnTop && (act.DiscountPercent != 0m || act.ManualTotalCents >= 0))
        {
            var correction = Money.Round2(act.Total-act.Materials-act.Smr-act.Transport-act.Pnr);
            totals.Add(("ВСЕГО до корректировки, с НДС",act.Total-correction));
            totals.Add((act.ManualTotalCents>=0?"Корректировка до согласованной суммы":
                "Скидка "+act.DiscountPercent.ToString("0.################",Ru)+"%",correction));
        }
        totals.Add((act.VatOnTop?"НДС сверху "+act.VatRate+"%":"В том числе НДС "+act.VatRate+"%",act.Vat));
        totals.Add(("ВСЕГО ПО АКТУ, с НДС",act.Total));
        if(pdf.Y>pdf.Bottom-(totals.Count*22+88))StartPage(true);
        foreach(var (caption,sum) in totals)
            pdf.Total(KsColumns.Sum(),caption,Amount(sum),22,
                caption.StartsWith("ВСЕГО",StringComparison.Ordinal));
        pdf.Y+=17;
        pdf.Text("Сдал: "+contractor.SignerPosition+" "+contractor.SignerName+"    ____________________",0,pdf.Y,750,18);
        pdf.Y+=25;
        pdf.Text("Принял: "+r.CustomerPosition+" "+r.CustomerName+"    ____________________",0,pdf.Y,750,18);
        if(!string.IsNullOrWhiteSpace(r.SecondRole))
        {
            pdf.Y+=25;
            pdf.Text(r.SecondRole+": "+r.SecondPosition+" "+r.SecondName+"    ____________________",
                0,pdf.Y,750,18);
        }
    }

    private static CultureInfo Invariant()=>CultureInfo.InvariantCulture;

    private static void DrawSummary(Canvas pdf,SqlServerKsPrintData.Package p)
    {
        var g=p.Report;
        var r=p.Requisites;
        pdf.NewPage();
        pdf.Text("Унифицированная форма № КС-3  •  ОКУД 0322001",0,pdf.Y,750,13,true);
        pdf.Y+=20;
        pdf.Text("Заказчик: "+Customer(r),0,pdf.Y,750,18);
        pdf.Y+=19;
        pdf.Text("Стройка: "+Site(r),0,pdf.Y,750,18);
        pdf.Y+=28;
        pdf.Text("СПРАВКА О СТОИМОСТИ ВЫПОЛНЕННЫХ РАБОТ И ЗАТРАТ",0,pdf.Y,750,18,true,13);
        pdf.Y+=27;
        pdf.Text("КС-3 №"+g.Key+"    Договор №"+g.AgreementNumber+
            "    Период: "+g.PeriodFrom?.ToString("dd.MM.yyyy")+" — "+
            g.PeriodTo?.ToString("dd.MM.yyyy"),0,pdf.Y,750,17);
        pdf.Y+=25;
        var columns=new double[]{32,275,90,110,116,103};
        pdf.Grid(columns,new[]{"№","Наименование работ и затрат","Код",
            "С начала работ, ₽","С начала года, ₽","За период, ₽"},36,true);
        pdf.Grid(columns,new[]{"1","Всего работ и затрат","",""+Amount(g.SinceStartNet),
            Amount(g.YearNet),Amount(g.PeriodNet)},33,false);
        pdf.Grid(columns,new[]{"2",string.IsNullOrWhiteSpace(r.WorkTitle)?"Выполненные работы":r.WorkTitle,
            "",Amount(g.SinceStartNet),Amount(g.YearNet),Amount(g.PeriodNet)},36,false);
        pdf.Total(columns.Sum(),"НДС за период",Amount(g.PeriodVat),23);
        pdf.Total(columns.Sum(),"ИТОГО с НДС",Amount(g.PeriodGross),25,true);
        pdf.Y+=12;
        pdf.Text("В состав КС-3 включены акты КС-2:",0,pdf.Y,750,16,true);
        pdf.Y+=19;
        foreach(var act in g.Acts)
        {
            if(pdf.Y>pdf.Bottom-45)
            {
                pdf.NewPage();
                pdf.Text("КС-3 №"+g.Key+" (продолжение)",0,pdf.Y,750,18,true,13);
                pdf.Y+=24;
            }
            pdf.Text("КС-2 №"+act.Number+" от "+act.Date.ToString("dd.MM.yyyy")+
                "   "+Amount(act.Total)+" ₽, в т.ч. НДС "+Amount(act.Vat)+" ₽",0,pdf.Y,750,19);
            pdf.Y+=20;
        }
        if(g.Previous.Count>0)
        {
            if(pdf.Y>pdf.Bottom-70)
            {
                pdf.NewPage();
                pdf.Y+=20;
            }
            pdf.Text("Предыдущие КС-3 для накопительного расчёта:",0,pdf.Y,750,17,true);
            pdf.Y+=20;
            foreach(var prior in g.Previous)
            {
                if(pdf.Y>pdf.Bottom-38) { pdf.NewPage(); pdf.Y+=15; }
                pdf.Text("№"+prior.Key+" от "+prior.Date.ToString("dd.MM.yyyy")+
                    "    Без НДС "+Amount(prior.Net)+" ₽    НДС "+Amount(prior.Vat)+" ₽"+
                    "    С НДС "+Amount(prior.Gross)+" ₽",0,pdf.Y,750,17);
                pdf.Y+=18;
            }
        }
        if(pdf.Y>pdf.Bottom-78)pdf.NewPage();
        pdf.Y+=14;
        pdf.Text("Принял: "+r.CustomerPosition+" "+r.CustomerName+"   ____________________",
            0,pdf.Y,750,18);
        if(!string.IsNullOrWhiteSpace(r.SecondRole))
        {
            pdf.Y+=25;
            pdf.Text(r.SecondRole+": "+r.SecondPosition+" "+r.SecondName+"   ____________________",
                0,pdf.Y,750,18);
        }
        var firstCo=p.Contractors[g.Acts[0].ContractId];
        pdf.Y+=25;
        pdf.Text("Сдал: "+firstCo.SignerPosition+" "+firstCo.SignerName+"   ____________________",
            0,pdf.Y,750,18);
    }

    private sealed class Canvas : IDisposable
    {
        private readonly PdfDocument _document;
        private XGraphics? _graphics;
        private readonly XFont _normal=new("Arial",9,XFontStyleEx.Regular);
        private readonly XFont _bold=new("Arial",9,XFontStyleEx.Bold);
        private readonly XFont _tiny=new("Arial",8,XFontStyleEx.Regular);
        private readonly XFont _title=new("Arial",13,XFontStyleEx.Bold);
        private const double Margin=37;
        public double Y {get;set;}
        public double Bottom=>555;
        public Canvas(PdfDocument document){_document=document;}

        public void NewPage()
        {
            _graphics?.Dispose();
            var page=_document.AddPage();
            page.Width=842;
            page.Height=595;
            _graphics=XGraphics.FromPdfPage(page);
            Y=Margin;
        }

        public void Text(string value,double x,double y,double width,double height,
            bool bold=false,int size=9)
        {
            var font=size>=13?_title:(size<=8?_tiny:(bold?_bold:_normal));
            _graphics!.DrawString(value,font,XBrushes.Black,
                new XRect(Margin+x,y,width,height),XStringFormats.TopLeft);
        }

        public double RowHeight(string value,double columnWidth)
        {
            return Math.Max(22,6+Wrap(value,columnWidth-6,_normal).Count*11.5);
        }

        private List<string> Wrap(string value,double width,XFont font)
        {
            var lines=new List<string>();
            var words=(value??"").Replace("\r","").Split(' ');
            string pending="";
            foreach(var word in words)
            {
                var proposal=pending.Length==0?word:pending+" "+word;
                if(_graphics!.MeasureString(proposal,font).Width<width || pending.Length==0)
                { pending=proposal;continue; }
                lines.Add(pending);
                pending=word;
            }
            if(pending.Length>0)lines.Add(pending);
            return lines.Count==0?new List<string>{""}:lines;
        }

        public void Grid(IReadOnlyList<double> columns,IReadOnlyList<string> values,double height,bool header)
        {
            double x=Margin;
            for(int col=0;col<columns.Count;col++)
            {
                _graphics!.DrawRectangle(new XPen(XColors.Black,0.55),
                    x,Y,columns[col],height);
                var font=header?_bold:_normal;
                var lines=Wrap(values[col],columns[col]-7,font);
                for(int i=0;i<lines.Count && i*11+7<height;i++)
                {
                    _graphics.DrawString(lines[i],font,XBrushes.Black,
                        new XRect(x+3,Y+3+i*11,columns[col]-6,11),XStringFormats.TopLeft);
                }
                x+=columns[col];
            }
            Y+=height;
        }

        public void Total(double tableWidth,string title,string value,double height,bool bold=false)
        {
            var rightWidth=110d;
            _graphics!.DrawRectangle(new XPen(XColors.Black,0.55),
                Margin,Y,tableWidth-rightWidth,height);
            _graphics.DrawRectangle(new XPen(XColors.Black,0.55),
                Margin+tableWidth-rightWidth,Y,rightWidth,height);
            var font=bold?_bold:_normal;
            _graphics.DrawString(title,font,XBrushes.Black,
                new XRect(Margin+5,Y+3,tableWidth-rightWidth-10,height-5),XStringFormats.TopLeft);
            _graphics.DrawString(value,font,XBrushes.Black,
                new XRect(Margin+tableWidth-rightWidth+5,Y+3,rightWidth-10,height-5),XStringFormats.TopLeft);
            Y+=height;
        }

        public void Dispose()=>_graphics?.Dispose();
    }
}
