using System.Globalization;
using System.IO;
using System.Text;
using GmcSmeta.Core.Domain;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace GmcSmeta.App;

/// <summary>
/// A4 page-layout analogue of smeta/passports/pdf.py. Equipment tables E.1.1-4 occupy
/// page 1; E.1.5-8 page 2. E.2 starts page 3 if it cannot fit intact on page 2.
/// Exact project/actual measurements are never synthesized.
/// </summary>
public static class VentPassportPdf
{
    public sealed record Result(byte[] Bytes, bool Draft, int Pages, int E2StartPage);

    private const double PageW = 595.276, PageH = 841.89;
    private const double Left = 34, Right = 34, Top = 40, Bottom = 42;
    private const double Width = PageW - Left - Right;
    private const double FooterY = PageH - 26;
    private const double CellPad = 3, LineHeight = 8.6;
    private static readonly XFont Tiny = new("Arial", 7, XFontStyleEx.Regular);
    private static readonly XFont TinyBold = new("Arial", 7, XFontStyleEx.Bold);
    private static readonly XFont Small = new("Arial", 8, XFontStyleEx.Regular);
    private static readonly XFont SmallBold = new("Arial", 9, XFontStyleEx.Bold);
    private static readonly XFont Title = new("Arial", 13, XFontStyleEx.Bold);
    private static readonly XFont DraftFont = new("Arial", 8, XFontStyleEx.Bold);

    public static Result Create(VentPassport passport)
    {
        VentPassports.Validate(passport);
        if (string.IsNullOrWhiteSpace(passport.System))
            throw new ArgumentException("Укажите обозначение системы.");
        var (projectTotal, actualTotal) = VentPassports.Totals(passport.Points);
        var draft = VentPassports.IsDraft(passport);
        using var pdf = new PdfDocument();
        pdf.Info.Title = "Паспорт системы вентиляции " + passport.System;
        pdf.Info.Subject = "Приложение Е к СП 73.13330.2016 изм. №1";
        pdf.Info.Author = passport.Organization;
        using var canvas = new Layout(pdf, passport.System, draft);

        canvas.Page();
        canvas.Text("Приложение Е к СП 73.13330.2016 изм. №1", Tiny);
        canvas.Text(passport.Organization, Small);
        canvas.Text("ПАСПОРТ СИСТЕМЫ ВЕНТИЛЯЦИИ " + passport.System, Title, gap: 10);
        canvas.Text("Объект: " + passport.Object);
        canvas.Text("Адрес: " + passport.Address);
        canvas.Text("Зона (цех): " + passport.Zone);
        canvas.Text("Дата: " + passport.Date);
        canvas.Text("Общие сведения", SmallBold, gap: 7);
        canvas.Text("Назначение: " + (string.IsNullOrWhiteSpace(passport.Purpose)
            ? VentPassports.DetectPurpose(passport.System) : passport.Purpose));
        canvas.Text("Местонахождение оборудования: " + passport.Location);
        canvas.Text("Е.1. Основные технические характеристики оборудования системы", SmallBold, gap: 7);
        for (int i = 0; i < 4; i++)
            DrawEquipment(canvas, passport, i, projectTotal, actualTotal);

        canvas.Page();
        for (int i = 4; i < 8; i++)
            DrawEquipment(canvas, passport, i, projectTotal, actualTotal);

        var e2Rows = BuildFlowRows(passport, projectTotal, actualTotal);
        var widths = new[] { Width * .13, Width * .37, Width * .17, Width * .17, Width * .16 };
        var tableHeight = canvas.TableHeight(e2Rows, widths);
        var conclusions = draft ? "Выводы: данные измерений не заполнены." :
            "Выводы: максимальное абсолютное отклонение расхода — " +
            VentPassports.Format(MaxDeviation(passport.Points)) + "%.";
        const string note = "Примечание — Допускается отклонение показателей по расходу воздуха ±10%, " +
                            "если воздухораздающие и воздухоприемные устройства расположены в одном помещении.";
        var e2Height = canvas.TextHeight("Е.2. Расходы воздуха по помещениям (по сети)", SmallBold, Width) +
                       5 + tableHeight + 8 +
                       canvas.TextHeight(conclusions, Small, Width) + 5 +
                       canvas.TextHeight(note, Tiny, Width);
        // If the complete E.2 doesn't fit on page 2, begin it wholly on page 3.
        if (canvas.Y + e2Height > PageH - Bottom)
            canvas.Page();
        var e2Page = canvas.PageNumber;
        canvas.Text("Е.2. Расходы воздуха по помещениям (по сети)", SmallBold, gap: 5);
        canvas.Table(e2Rows, widths, permitSplit: true, repeatHeader: true);
        canvas.Gap(8);
        canvas.Text(conclusions);
        canvas.Text(note, Tiny);
        canvas.Finish();
        using var stream = new MemoryStream();
        pdf.Save(stream, false);
        return new Result(stream.ToArray(), draft, canvas.PageNumber, e2Page);
    }

    private static void DrawEquipment(Layout canvas, VentPassport doc, int index,
        decimal? projectTotal, decimal? actualTotal)
    {
        var spec = VentPassports.Sections[index];
        var source = doc.Equipment[index];
        var project = source.Project.ToArray();
        var actual = source.Actual.ToArray();
        var notes = source.Notes;
        if (index == 0)
        {
            project[3] = VentPassports.Format(projectTotal);
            actual[3] = VentPassports.Format(actualTotal);
        }
        if (index == 6)
        {
            Array.Fill(project, ""); Array.Fill(actual, "");
            notes = "";
        }
        if (index == 7)
        {
            notes = doc.Filter ? "" : "пылегазоулавливающее устройство отсутствует";
            if (!doc.Filter)
            {
                Array.Fill(project, ""); Array.Fill(actual, "");
            }
        }
        var headings = new[] { "Данные" }.Concat(spec.Parameters).ToArray();
        var rows = new List<string[]>
        {
            headings,
            new[] { "По проекту" }.Concat(project).ToArray(),
            new[] { "Фактически" }.Concat(actual).ToArray()
        };
        var columnWidths = new double[headings.Length];
        columnWidths[0] = 68;
        for (int i = 1; i < columnWidths.Length; i++)
            columnWidths[i] = (Width - columnWidths[0]) / (columnWidths.Length - 1);
        var name = $"Таблица Е.1.{index + 1} — {spec.Title}";
        var captionHeight = canvas.TextHeight(name, SmallBold, Width) + 4;
        var tableHeight = canvas.TableHeight(rows, columnWidths);
        var notesHeight = canvas.TextHeight("Примечания: " + notes, Tiny, Width) + 5;
        canvas.RequireEquipmentSpace(captionHeight + tableHeight + notesHeight + 4);
        canvas.Text(name, SmallBold, gap: 4);
        canvas.Table(rows, columnWidths, permitSplit: false);
        canvas.Text("Примечания: " + notes, Tiny, gap: 9);
    }

    private static List<string[]> BuildFlowRows(VentPassport p, decimal? project, decimal? actual)
    {
        var data = new List<string[]>
        {
            new[] { "Мерное сечение","Помещение","Фактически, м³/ч","По проекту, м³/ч","Невязка, %" }
        };
        for (int i = 0; i < p.Points.Count; i++)
        {
            var point = p.Points[i];
            var pr = VentPassports.Flow(point.Project);
            var ac = VentPassports.Flow(point.Actual);
            decimal? deviation = pr is > 0 && ac is not null ? (ac.Value - pr.Value) / pr.Value * 100m : null;
            data.Add([
                string.IsNullOrWhiteSpace(point.Section) ? (i + 1).ToString(CultureInfo.InvariantCulture) : point.Section,
                point.Room, VentPassports.Format(ac), VentPassports.Format(pr), VentPassports.Format(deviation)
            ]);
        }
        data.Add(["","Итого",VentPassports.Format(actual),VentPassports.Format(project),""]);
        return data;
    }

    private static decimal MaxDeviation(IEnumerable<VentFlowPoint> items)
    {
        decimal max = 0m;
        foreach (var p in items)
        {
            var project = VentPassports.Flow(p.Project);
            var actual = VentPassports.Flow(p.Actual);
            if (project is > 0 && actual is not null)
            {
                var deviation = Math.Abs((actual.Value - project.Value) / project.Value * 100m);
                if (deviation > max) max = deviation;
            }
        }
        return max;
    }

    private sealed class Layout : IDisposable
    {
        private readonly PdfDocument _document;
        private readonly string _system;
        private readonly bool _draft;
        private XGraphics? _graphics;
        public int PageNumber { get; private set; }
        public double Y { get; private set; }

        public Layout(PdfDocument document, string system, bool draft)
        { _document = document; _system = system; _draft = draft; }

        public void Page()
        {
            Footer();
            var page = _document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            _graphics = XGraphics.FromPdfPage(page);
            PageNumber++;
            Y = Top;
            if (_draft)
                _graphics.DrawString("ЧЕРНОВИК — данные измерений не заполнены", DraftFont, XBrushes.Firebrick,
                    new XRect(Left, 13, Width, 15), XStringFormats.TopCenter);
        }

        private void Footer()
        {
            if (_graphics is null) return;
            _graphics.DrawString(_system, Tiny, XBrushes.Black,
                new XRect(Left, FooterY, Width / 2, 14), XStringFormats.TopLeft);
            _graphics.DrawString(PageNumber.ToString(CultureInfo.InvariantCulture), Tiny, XBrushes.Black,
                new XRect(Left + Width / 2, FooterY, Width / 2, 14), XStringFormats.TopRight);
            _graphics.Dispose();
            _graphics = null;
        }

        public void Finish() => Footer();
        public void Dispose() => Footer();
        public void Gap(double size) { if (Y + size > PageH - Bottom) Page(); Y += size; }

        public double TextHeight(string? value, XFont font, double width)
        {
            var lines = Wrap(value, font, width);
            return Math.Max(1, lines.Count) * (font.Size + 2.4);
        }

        public void RequireEquipmentSpace(double height)
        {
            if (Y + height > PageH - Bottom)
                throw new InvalidDataException(
                    "Текст оборудования не помещается на двух листах. Сократите длинные описания в полях паспорта.");
        }

        public void Text(string? value, XFont? font = null, double gap = 4)
        {
            font ??= Small;
            var lines = Wrap(value, font, Width);
            var line = font.Size + 2.4;
            if (Y + lines.Count * line > PageH - Bottom)
                throw new InvalidDataException("Текст оборудования или выводов слишком длинный для страницы.");
            foreach (var part in lines)
            {
                _graphics!.DrawString(part, font, XBrushes.Black,
                    new XRect(Left, Y, Width, line), XStringFormats.TopLeft);
                Y += line;
            }
            Y += gap;
        }

        private List<string> Wrap(string? value, XFont font, double maxWidth)
        {
            var result = new List<string>();
            foreach (var paragraph in (value ?? "").Replace("\r", "").Split('\n'))
            {
                if (paragraph.Length == 0) { result.Add(""); continue; }
                var current = "";
                foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var next = current.Length == 0 ? word : current + " " + word;
                    if (_graphics!.MeasureString(next, font).Width <= maxWidth)
                    { current = next; continue; }
                    if (current.Length > 0) { result.Add(current); current = ""; }
                    foreach (var ch in word)
                    {
                        var candidate = current + ch;
                        if (current.Length > 0 && _graphics!.MeasureString(candidate, font).Width > maxWidth)
                        { result.Add(current); current = ch.ToString(); }
                        else current = candidate;
                    }
                }
                result.Add(current);
            }
            return result.Count == 0 ? [""] : result;
        }

        public double TableHeight(IReadOnlyList<string[]> rows, IReadOnlyList<double> widths)
        {
            var sum = 0d;
            foreach (var row in rows) sum += RowHeight(row, widths);
            return sum;
        }

        private double RowHeight(IReadOnlyList<string> row, IReadOnlyList<double> widths)
        {
            if (row.Count != widths.Count) throw new ArgumentException("Количество столбцов не совпадает.");
            return Math.Max(16, Enumerable.Range(0, row.Count)
                .Max(i => TextHeight(row[i], Tiny, Math.Max(1, widths[i] - CellPad * 2)) +
                          CellPad * 2 + 1));
        }

        private void Row(IReadOnlyList<string> values, IReadOnlyList<double> widths)
        {
            var h = RowHeight(values, widths);
            if (h > PageH - Top - Bottom)
                throw new InvalidDataException("Слишком длинный текст в одной строке таблицы.");
            var x = Left;
            for (int i = 0; i < values.Count; i++)
            {
                _graphics!.DrawRectangle(XPens.Black, x, Y, widths[i], h);
                var yy = Y + CellPad;
                foreach (var segment in Wrap(values[i], Tiny, Math.Max(1, widths[i] - CellPad * 2)))
                {
                    _graphics.DrawString(segment, Tiny, XBrushes.Black,
                        new XRect(x + CellPad, yy, widths[i] - CellPad * 2, LineHeight),
                        XStringFormats.TopLeft);
                    yy += Tiny.Size + 2.4;
                }
                x += widths[i];
            }
            Y += h;
        }

        public void Table(IReadOnlyList<string[]> rows, IReadOnlyList<double> widths,
            bool permitSplit, bool repeatHeader = false)
        {
            if (rows.Count == 0) return;
            var entire = TableHeight(rows, widths);
            if (!permitSplit && Y + entire > PageH - Bottom)
                throw new InvalidDataException("Текст оборудования не помещается на двух листах.");
            for (int i = 0; i < rows.Count; i++)
            {
                var needed = RowHeight(rows[i], widths);
                if (Y + needed > PageH - Bottom)
                {
                    if (!permitSplit)
                        throw new InvalidDataException("Текст оборудования не помещается на двух листах.");
                    Page();
                    if (repeatHeader && i > 0)
                    {
                        var title = "Е.2. Расходы воздуха по помещениям (по сети) — продолжение";
                        Text(title, SmallBold, gap: 5);
                        Row(rows[0], widths);
                    }
                }
                Row(rows[i], widths);
            }
        }
    }
}
