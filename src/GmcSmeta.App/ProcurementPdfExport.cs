using System.Globalization;
using System.IO;
using GmcSmeta.Core.Db;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace GmcSmeta.App;

/// <summary>Printable A4 procurement and workshop documents, with repeated table headers.</summary>
public static class ProcurementPdfExport
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static byte[] CreateControl(ProcurementControlExport.Snapshot data, bool withMoney = false)
    {
        var headers = new List<string>
        {
            "№", "Наименование", "Ед.", "По КП", "Поставщик", "Статус",
            "Заказ / счёт", "Пришло", "Выдано", "Примечание / замена"
        };
        var columns = new List<double> { 30, 200, 40, 48, 67, 79, 90, 46, 46, 126 };
        if (withMoney)
        {
            headers.AddRange(new[] { "Себест.", "Цена", "Маржа" });
            columns = new List<double> { 25, 166, 35, 41, 60, 66, 61, 43, 43, 92, 44, 44, 49 };
        }
        using var doc = new PdfDocument();
        doc.Info.Title = "Контроль оборудования — " + data.Document.ObjectName;
        using var paint = new Painter(doc, landscape: true);
        var number = 1;

        void Start(bool continuation)
        {
            paint.NewPage();
            paint.Paragraph(data.Document.ObjectName + " — контроль оборудования" +
                (continuation ? " (продолжение)" : ""), 16, true);
            paint.Paragraph(data.Document.Title + " · " + DateTime.Today.ToString("dd.MM.yyyy", Ru), 11);
            paint.Move(7);
            paint.Header(headers, columns);
        }
        Start(false);
        foreach (var group in data.Rows.Where(x => !x.Work).GroupBy(x => x.SystemId))
        {
            var first = group.First();
            if (first.SystemName != "Без системы")
            {
                if (paint.Space < 35) Start(true);
                var section = new string[headers.Count];
                section[0] = (number++).ToString(Ru);
                section[1] = first.SystemName;
                paint.Row(section, columns, sectionRow: true);
            }
            foreach (var row in group)
            {
                var order = string.Join(" / ", new[]
                {
                    row.OrderRef,
                    row.InvoiceRef.Length > 0 ? "сч. " + row.InvoiceRef : ""
                }.Where(x => x.Length > 0));
                if (row.DueDate is not null && row.Status is not ("issued" or "stock"))
                    order += (row.DueDate.Value < DateTime.Today ? " ПРОСРОЧЕНО к " : " ждём к ") +
                        row.DueDate.Value.ToString("dd.MM.yy", Ru);
                var note = (row.Replacement.Length > 0 ? "замена: " + row.Replacement : "") +
                    (row.Note.Length > 0 ? (row.Replacement.Length > 0 ? "; " : "") + row.Note : "");
                var values = new List<string>
                {
                    row.OutOfKp ? "" : (number++).ToString(Ru),
                    row.Name + (row.OutOfKp ? " [вне КП]" : ""),
                    row.Unit, row.Qty.ToString("0.###", Ru), row.Supplier ?? "",
                    row.StatusLabel, order,
                    row.Received == 0m ? "" : row.Received.ToString("0.###", Ru),
                    row.Issued == 0m ? "" : row.Issued.ToString("0.###", Ru),
                    note
                };
                if (withMoney)
                {
                    values.Add(row.CostPrice?.ToString("N2", Ru) ?? "");
                    values.Add(row.OutOfKp ? "" : row.Price.ToString("N2", Ru));
                    values.Add(row.OutOfKp || row.CostPrice is null ? "" :
                        ((row.Price - row.CostPrice.Value) * row.Qty).ToString("N2", Ru));
                }
                var height = paint.MeasureRow(values, columns);
                if (paint.Space < height + 8) Start(true);
                paint.Row(values, columns, height);
            }
        }
        return paint.ToBytes();
    }

    public static byte[] CreateShopOrder(
        ProcurementRequestPlanner.DocumentInfo document, IReadOnlyList<SqlServerProcurement.BuyRow> positions,
        string number = "", string due = "") =>
        CreateShopOrder(document, ProcurementRequestPlanner.ShopPrintRows(positions), number, due);

    public static byte[] CreateShopOrder(
        ProcurementRequestPlanner.DocumentInfo document,
        IReadOnlyList<ProcurementRequestPlanner.ShopPrintLine> positions,
        string number = "", string due = "")
    {
        if (ProcurementRequestPlanner.ShopPrintError(positions) is { } error)
            throw new InvalidOperationException(error);
        var headers = new[] { "№", "Система", "Наименование", "Ед.", "Кол-во", "Примечание" };
        var columns = new double[] { 26, 78, 243, 37, 45, 90 };
        using var doc = new PdfDocument();
        doc.Info.Title = "Заявка в цех — " + document.ObjectName;
        using var paint = new Painter(doc, landscape: false);
        void Start(bool continuation)
        {
            paint.NewPage();
            if (!string.IsNullOrWhiteSpace(document.CompanyHeader))
                paint.Paragraph(document.CompanyHeader, 10, true);
            paint.Paragraph("ЗАЯВКА В ЦЕХ" +
                (string.IsNullOrWhiteSpace(number) ? "" : " № " + number) +
                " от " + DateTime.Today.ToString("dd.MM.yyyy", Ru) +
                (continuation ? " (продолжение)" : ""), 16, true);
            paint.Paragraph("Объект: " + document.ObjectName + " · " + document.Title +
                (string.IsNullOrWhiteSpace(due) ? "" : " · срок: " + due), 11);
            paint.Move(10);
            paint.Header(headers, columns);
        }
        Start(false);
        var i = 0;
        foreach (var row in positions)
        {
            var values = new[]
            {
                (++i).ToString(Ru), row.System, row.Name, row.Unit,
                row.Quantity.ToString("0.######", Ru), row.Note
            };
            var height = paint.MeasureRow(values, columns);
            if (paint.Space < height + 75) Start(true);
            paint.Row(values, columns, height);
        }
        if (paint.Space < 75) paint.NewPage();
        paint.Move(32);
        paint.Paragraph("Выдал: ____________________         Принял (цех): ____________________", 11);
        return paint.ToBytes();
    }

    private sealed class Painter : IDisposable
    {
        private readonly PdfDocument _doc;
        private readonly bool _landscape;
        private readonly XFont _regular = new("Arial", 8, XFontStyleEx.Regular);
        private readonly XFont _bold = new("Arial", 8, XFontStyleEx.Bold);
        private readonly XFont _big = new("Arial", 13, XFontStyleEx.Bold);
        private XGraphics? _g;
        private readonly double _margin = 35;
        private double _bottom;
        private double _y;
        public double Space => _bottom - _y;

        public Painter(PdfDocument doc, bool landscape)
        {
            _doc = doc; _landscape = landscape;
        }

        public void NewPage()
        {
            _g?.Dispose();
            var page = _doc.AddPage();
            page.Width = _landscape ? 842 : 595;
            page.Height = _landscape ? 595 : 842;
            _bottom = (double)page.Height - 40;
            _y = _margin;
            _g = XGraphics.FromPdfPage(page);
        }

        public void Paragraph(string text, double height, bool bold = false)
        {
            if (_g is null) throw new InvalidOperationException("No page.");
            _g.DrawString(text ?? "", height >= 14 ? _big : (bold ? _bold : _regular),
                XBrushes.Black,
                new XRect(_margin, _y, _landscape ? 760 : 520, Math.Max(15, height + 4)),
                XStringFormats.TopLeft);
            _y += Math.Max(height + 5, 18);
        }

        public void Move(double value) => _y += value;

        public void Header(IReadOnlyList<string> headings, IReadOnlyList<double> widths) =>
            Row(headings, widths, 32, sectionRow: true);

        public double MeasureRow(IReadOnlyList<string> values, IReadOnlyList<double> widths)
        {
            if (_g is null) throw new InvalidOperationException("No page.");
            double height = 22;
            for (var i = 0; i < widths.Count; i++)
            {
                var lines = Wrap(values[i] ?? "", widths[i] - 7, _regular);
                height = Math.Max(height, lines.Count * 10.5 + 7);
            }
            return Math.Min(180, height);
        }

        private List<string> Wrap(string text, double maxWidth, XFont font)
        {
            var result = new List<string>();
            foreach (var paragraph in text.Replace("\r", "").Split('\n'))
            {
                var line = "";
                foreach (var token in paragraph.Split(' '))
                {
                    var next = line.Length == 0 ? token : line + " " + token;
                    if (line.Length == 0 || _g!.MeasureString(next, font).Width <= maxWidth)
                    {
                        line = next;
                        continue;
                    }
                    result.Add(line);
                    line = token;
                }
                result.Add(line);
            }
            return result;
        }

        public void Row(IReadOnlyList<string> text, IReadOnlyList<double> widths,
            double height = 0, bool sectionRow = false)
        {
            if (_g is null) throw new InvalidOperationException("No page.");
            if (height <= 0) height = MeasureRow(text, widths);
            var x = _margin;
            for (var i = 0; i < widths.Count; i++)
            {
                _g.DrawRectangle(new XPen(XColors.Black, 0.55),
                    x, _y, widths[i], height);
                var font = sectionRow ? _bold : _regular;
                var lines = Wrap(text[i] ?? "", widths[i] - 7, font);
                for (var j = 0; j < lines.Count && 7 + (j + 1) * 10 < height + 6; j++)
                    _g.DrawString(lines[j], font, XBrushes.Black,
                        new XRect(x + 3, _y + 3 + j * 10, widths[i] - 6, 11),
                        XStringFormats.TopLeft);
                x += widths[i];
            }
            _y += height;
        }

        public byte[] ToBytes()
        {
            _g?.Dispose();
            _g = null;
            using var stream = new MemoryStream();
            _doc.Save(stream, false);
            return stream.ToArray();
        }

        public void Dispose() => _g?.Dispose();
    }
}
