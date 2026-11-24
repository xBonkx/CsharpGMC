using System.Globalization;
using System.IO;
using GmcSmeta.Core.Db;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace GmcSmeta.App;

/// <summary>Printable A4 issue list. Renders from the same read-only snapshot as XLSX.</summary>
public static class ShipmentPdfForms
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly double[] Widths = { 29, 253, 34, 54, 60, 75 };
    private const double Left = 42;
    private const double Bottom = 785;
    private static readonly XFont Normal = new("Arial", 9, XFontStyleEx.Regular);
    private static readonly XFont Bold = new("Arial", 9, XFontStyleEx.Bold);
    private static readonly XFont Title = new("Arial", 13, XFontStyleEx.Bold);

    public static byte[] Create(SqlServerShipmentPrintData.Snapshot shipment)
    {
        ArgumentNullException.ThrowIfNull(shipment);
        using var pdf = new PdfDocument();
        pdf.Info.Title = "Ведомость выдачи № " + shipment.Number;
        XGraphics? graphics = null;
        double y = 0;
        var pageNo = 0;

        void StartPage()
        {
            graphics?.Dispose();
            var page = pdf.AddPage();
            page.Width = 595;
            page.Height = 842;
            graphics = XGraphics.FromPdfPage(page);
            pageNo++;
            y = 39;
            var date = shipment.Date == DateTime.MinValue ? "" : " от " + shipment.Date.ToString("dd.MM.yyyy");
            graphics.DrawString("Ведомость выдачи № " + shipment.Number + date,
                Title, XBrushes.Black, new XRect(Left, y, 500, 22), XStringFormats.TopLeft);
            y += 29;
            graphics.DrawString("Объект: " + shipment.ObjectName, Normal, XBrushes.Black,
                new XRect(Left, y, 505, 14), XStringFormats.TopLeft);
            y += 16;
            if (shipment.Recipient.Length > 0)
            {
                graphics.DrawString("Получатель: " + shipment.Recipient, Normal, XBrushes.Black,
                    new XRect(Left, y, 505, 14), XStringFormats.TopLeft);
                y += 18;
            }
            y += 10;
            Grid(new[] { "№", "Наименование", "Ед.", "Кол-во", "Получено", "Примечание" }, 27, true, true);
        }

        List<string> Wrap(string text, double width, XFont font)
        {
            var lines = new List<string>();
            foreach (var paragraph in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var pending = "";
                foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = pending.Length == 0 ? word : pending + " " + word;
                    if (graphics!.MeasureString(candidate, font).Width <= width)
                    {
                        pending = candidate;
                        continue;
                    }
                    if (pending.Length > 0) { lines.Add(pending); pending = ""; }
                    // Long unspaced product codes must not bleed into adjacent cells.
                    var fragment = "";
                    foreach (var ch in word)
                    {
                        if (fragment.Length > 0 &&
                            graphics.MeasureString(fragment + ch, font).Width > width)
                        {
                            lines.Add(fragment);
                            fragment = "";
                        }
                        fragment += ch;
                    }
                    pending = fragment;
                }
                lines.Add(pending);
            }
            return lines.Count > 0 ? lines : new List<string> { "" };
        }

        double Height(string[] cells, bool header = false)
        {
            double height = header ? 27 : 20;
            for (var i = 0; i < cells.Length; i++)
                height = Math.Max(height, 6 + Wrap(cells[i], Widths[i] - 8, header ? Bold : Normal).Count * 12);
            return height;
        }

        void Grid(string[] cells, double height, bool bold = false, bool shaded = false)
        {
            double x = Left;
            for (var i = 0; i < Widths.Length; i++)
            {
                if (shaded) graphics!.DrawRectangle(XBrushes.LightGray, x, y, Widths[i], height);
                graphics!.DrawRectangle(new XPen(XColors.Gray, 0.5), x, y, Widths[i], height);
                var font = bold ? Bold : Normal;
                var lines = Wrap(cells[i], Widths[i] - 8, font);
                for (var j = 0; j < lines.Count && 4 + j * 12 < height; j++)
                    graphics.DrawString(lines[j], font, XBrushes.Black,
                        new XRect(x + 4, y + 3 + j * 12, Widths[i] - 8, 12), XStringFormats.TopLeft);
                x += Widths[i];
            }
            y += height;
        }

        StartPage();
        var multipleDocs = shipment.Lines.Select(x => x.Document).Distinct().Skip(1).Any();
        var group = "";
        var index = 0;
        foreach (var line in shipment.Lines)
        {
            var currentGroup = line.Document + "\u001f" + line.System;
            if (group != currentGroup)
            {
                group = currentGroup;
                var label = multipleDocs ? line.Document + " · " + line.System : line.System;
                var cells = new[] { "", label, "", "", "", "" };
                var h = Height(cells, true);
                if (y + h > Bottom) StartPage();
                Grid(cells, h, true, true);
            }
            var row = new[]
            {
                (++index).ToString(CultureInfo.InvariantCulture), line.Name,
                line.Unit, line.Quantity.ToString("0.###", Ru), "", line.Note
            };
            var height = Height(row);
            if (y + height > Bottom) StartPage();
            Grid(row, height);
        }

        if (y > Bottom - 100) StartPage();
        y += 15;
        graphics!.DrawString("Всего позиций: " + index, Normal, XBrushes.Black,
            new XRect(Left, y, 500, 16), XStringFormats.TopLeft);
        y += 23;
        if (shipment.Note.Length > 0)
        {
            var noteLines = Wrap("Примечание: " + shipment.Note, 500, Normal);
            foreach (var line in noteLines)
            {
                if (y + 18 > Bottom) StartPage();
                graphics.DrawString(line, Normal, XBrushes.Black,
                    new XRect(Left, y, 500, 13), XStringFormats.TopLeft);
                y += 14;
            }
            y += 15;
        }
        if (y > Bottom - 75) StartPage();
        graphics.DrawString("Выдал: _______________ " + shipment.IssuedBy, Normal,
            XBrushes.Black, new XRect(Left, y, 245, 18), XStringFormats.TopLeft);
        graphics.DrawString("Получил: _______________ " + shipment.Recipient, Normal,
            XBrushes.Black, new XRect(Left + 260, y, 245, 18), XStringFormats.TopLeft);
        y += 28;
        graphics.DrawString("Дата: «____» ______________ 20___ г.", Normal,
            XBrushes.Black, new XRect(Left, y, 300, 18), XStringFormats.TopLeft);
        graphics.Dispose();

        using var memory = new MemoryStream();
        pdf.Save(memory, false);
        return memory.ToArray();
    }
}
