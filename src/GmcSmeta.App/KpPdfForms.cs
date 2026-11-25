using System.Globalization;
using System.IO;
using GmcSmeta.Core.Domain;
using GmcSmeta.Core.Db;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace GmcSmeta.App;

/// <summary>
/// Multi-page КП PDF from the same estimate snapshot as Excel; explicitly does not
/// substitute customer-supplied special PDF templates that are not in the repository.
/// </summary>
public static class KpPdfForms
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly XFont Normal = new("Arial", 8, XFontStyleEx.Regular);
    private static readonly XFont Bold = new("Arial", 8, XFontStyleEx.Bold);
    private static readonly XFont Title = new("Arial", 13, XFontStyleEx.Bold);

    public static byte[] Create(EstimateDocument document, DocRow header,
        bool internalPrices = false, bool positional = false, bool vatLine = false,
        SqlServerKpRequisites.Values? requisites = null, bool manualNumbering = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(header);
        if (document.ContractId != header.Id)
            throw new InvalidOperationException("Не совпадает документ в экспорте.");
        if (internalPrices && positional)
            throw new ArgumentException("Нельзя объединить две несовместимые формы.");

        var landscape = internalPrices || positional;
        var widths = positional ? new[] { 34d, 309d, 59d, 65d, 68d, 75d, 70d, 82d }
            : internalPrices ? new[] { 32d, 285d, 45d, 55d, 62d, 70d, 62d, 70d, 80d }
            : new[] { 28d, 244d, 42d, 50d, 75d, 85d };
        var heads = positional
            ? new[] { "Поз.", "Наименование и техническая характеристика", "Ед.", "Кол-во", "Мат./ед.", "Мат. всего", "Раб./ед.", "Раб. всего" }
            : internalPrices
                ? new[] { "Поз.", "Наименование и техническая характеристика", "Ед.", "Кол-во", "Закуп/ед.", "Закуп всего", "Розн./ед.", "Розн. всего", "Маржа" }
                : new[] { "Поз.", "Наименование и техническая характеристика", "Ед.", "Кол-во", "Цена", "Всего" };
        var totalCol = positional ? 5 : internalPrices ? 7 : 5;

        using var pdf = new PdfDocument();
        pdf.Info.Title = header.Title;
        pdf.Info.Subject = "КП / смета — GMC-Смета";
        XGraphics? gfx = null;
        var pageNumber = 0;
        var y = 0d;
        var left = landscape ? 40d : 35d;
        var bottom = landscape ? 535d : 785d;
        MemoryStream? logoStream = null;
        XImage? logo = null;
        if (requisites?.CompanyLogo is { Length: > 16 } pngOrJpeg)
        {
            try
            {
                logoStream = new MemoryStream(pngOrJpeg, 0, pngOrJpeg.Length,
                    writable: false, publiclyVisible: true);
                logo = XImage.FromStream(logoStream);
            }
            catch (Exception)
            {
                // Python also omits an unsupported or damaged logo, without
                // invalidating the otherwise printable commercial document.
                logo?.Dispose();
                logoStream?.Dispose();
                logo = null;
                logoStream = null;
            }
        }

        List<string> Wrap(string value, double width, XFont font)
        {
            var output = new List<string>();
            foreach (var part in (value ?? "").Replace("\r", "").Split('\n'))
            {
                var pending = "";
                foreach (var word in part.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var next = pending.Length == 0 ? word : pending + " " + word;
                    if (gfx!.MeasureString(next, font).Width < width)
                    {
                        pending = next;
                        continue;
                    }
                    if (pending.Length != 0) output.Add(pending);
                    pending = "";
                    foreach (var letter in word)
                    {
                        if (pending.Length > 0 &&
                            gfx.MeasureString(pending + letter, font).Width >= width)
                        {
                            output.Add(pending);
                            pending = "";
                        }
                        pending += letter;
                    }
                }
                output.Add(pending);
            }
            return output.Count > 0 ? output : new List<string> { "" };
        }

        double Height(IReadOnlyList<string> values, bool strong = false)
        {
            var result = strong ? 27d : 20d;
            for (var i = 0; i < values.Count; i++)
                result = Math.Max(result, 5 + Wrap(values[i], widths[i] - 7,
                    strong ? Bold : Normal).Count * 10d);
            return result;
        }

        void Grid(IReadOnlyList<string> values, double height, bool strong = false, bool shaded = false)
        {
            var x = left;
            for (var i = 0; i < widths.Length; i++)
            {
                if (shaded) gfx!.DrawRectangle(XBrushes.LightGray, x, y, widths[i], height);
                gfx!.DrawRectangle(new XPen(XColors.Gray, .5), x, y, widths[i], height);
                var font = strong ? Bold : Normal;
                var words = Wrap(values[i], widths[i] - 7, font);
                for (var line = 0; line < words.Count && line * 10 + 5 < height; line++)
                    gfx.DrawString(words[line], font, XBrushes.Black,
                        new XRect(x + 3, y + 3 + line * 10, widths[i] - 6, 10),
                        XStringFormats.TopLeft);
                x += widths[i];
            }
            y += height;
        }

        void NewPage()
        {
            gfx?.Dispose();
            var page = pdf.AddPage();
            page.Width = landscape ? 842 : 595;
            page.Height = landscape ? 595 : 842;
            gfx = XGraphics.FromPdfPage(page);
            pageNumber++;
            y = 33;
            if (logo is not null)
            {
                var height = 70d;
                var width = Math.Min(widths.Sum() / 3 - 12,
                    height * logo.PixelWidth / Math.Max(1d, logo.PixelHeight));
                gfx.DrawImage(logo, left, y, width, height);
            }
            if (requisites is { CompanyHeader.Count: > 0 })
            {
                foreach (var line in requisites.CompanyHeader)
                {
                    gfx.DrawString(line, Normal, XBrushes.Black,
                        new XRect(left + widths.Sum() / 3, y, widths.Sum() * 2 / 3, 13),
                        XStringFormats.TopRight);
                    y += 13;
                }
                y += 12;
            }
            if (logo is not null) y = Math.Max(y, 33 + 78);
            var title = header.Kind switch
            {
                "main" => "Приложение № 1 к Договору подряда",
                "dop" => "Приложение № 1 к Дополнительному соглашению",
                _ => "Коммерческое предложение"
            };
            if (header.Kind != "kp" && header.Number.Length > 0)
                title += " № " + header.Number;
            if (header.Kind != "kp" && header.Date.Length > 0)
                title += " от " + header.Date + " г.";
            gfx.DrawString(title, Title, XBrushes.Black,
                new XRect(left, y, widths.Sum(), 23), XStringFormats.TopCenter);
            y += 26;
            if (header.Kind == "dop" && requisites?.BaseContractNumber.Length > 0)
            {
                gfx.DrawString("к Договору подряда № " + requisites.BaseContractNumber +
                    (requisites.BaseContractDate.Length > 0
                        ? " от " + requisites.BaseContractDate + " г." : ""),
                    Normal, XBrushes.Black, new XRect(left, y, widths.Sum(), 14),
                    XStringFormats.TopCenter);
                y += 16;
            }
            gfx.DrawString(document.ObjectName +
                (header.Name.Length > 0 ? " — " + header.Name : ""), Bold, XBrushes.Black,
                new XRect(left, y, widths.Sum(), 18), XStringFormats.TopCenter);
            y += 20;
            if (document.Company.Length > 0)
            {
                gfx.DrawString(document.Company, Normal, XBrushes.Black,
                    new XRect(left, y, widths.Sum(), 15), XStringFormats.TopRight);
                y += 18;
            }
            y += 6;
            Grid(heads, Height(heads, true), true, true);
        }

        void Add(IReadOnlyList<string> cells, bool strong = false, bool shaded = false)
        {
            var height = Height(cells, strong);
            if (y + height > bottom) NewPage();
            Grid(cells, height, strong, shaded);
        }

        NewPage();
        var areaRoots = document.Rows.Where(x => x.IsAreaSummary).Select(x => x.Id).ToHashSet();
        var excludedParents = document.Rows.Where(x => !x.IsSystem && x.OutOfKp)
            .Select(x => x.Id).ToHashSet();
        var skippedSystems = KpExcelForms.HiddenSystems(document.Rows, areaRoots, excludedParents);
        var number = 1;
        decimal printedPurchaseTotal = 0m;
        decimal printedMaterialTotal = 0m;
        foreach (var row in document.Rows)
        {
            if (row.IsSystem)
            {
                if (row.Name == "Без системы" || skippedSystems.Contains(row.SystemId)) continue;
                var systemCells = new string[widths.Length];
                systemCells[0] = !positional && manualNumbering ? row.Num : number.ToString();
                number++;
                systemCells[1] = row.Name;
                if (!positional)
                    for (var c = 2; c < systemCells.Length; c++) systemCells[c] = "*";
                Add(systemCells, true, true);
                continue;
            }
            if (row.OutOfKp || (row.ParentPositionId.HasValue &&
                (areaRoots.Contains(row.ParentPositionId.Value) ||
                 excludedParents.Contains(row.ParentPositionId.Value)))) continue;
            var cells = new string[widths.Length];
            cells[0] = !positional && manualNumbering
                ? row.Num : row.IsSub ? "" : (number++).ToString();
            cells[1] = row.Name;
            cells[2] = row.Unit;
            cells[3] = Quantity(row.Qty);
            if (!row.IsSub)
            {
                printedMaterialTotal += row.Qty * row.Price;
                if (positional)
                {
                    cells[4] = Amount(row.Price);
                    cells[5] = Amount(row.Qty * row.Price);
                    cells[6] = Amount(row.WorkPrice ?? 0m);
                    cells[7] = Amount(row.Qty * (row.WorkPrice ?? 0m));
                }
                else if (internalPrices)
                {
                    if (row.PurchasePrice.HasValue)
                        printedPurchaseTotal += row.Qty * row.PurchasePrice.Value;
                    cells[4] = row.PurchasePrice.HasValue ? Amount(row.PurchasePrice.Value) : "";
                    cells[5] = row.PurchasePrice.HasValue ? Amount(row.Qty * row.PurchasePrice.Value) : "";
                    cells[6] = Amount(row.Price);
                    cells[7] = Amount(row.Qty * row.Price);
                    cells[8] = row.PurchasePrice.HasValue
                        ? Amount(row.Qty * (row.Price - row.PurchasePrice.Value)) : "";
                }
                else
                {
                    cells[4] = Amount(row.Price);
                    cells[5] = Amount(row.Qty * row.Price);
                }
            }
            Add(cells);
        }
        void Total(string name, decimal? material, decimal? work = null, bool bold = false,
            decimal? purchase = null, decimal? margin = null)
        {
            var values = new string[widths.Length];
            values[1] = name;
            if (material.HasValue) values[totalCol] = Amount(material.Value);
            if (positional && work.HasValue) values[7] = Amount(work.Value);
            if (internalPrices && purchase.HasValue) values[5] = Amount(purchase.Value);
            if (internalPrices && margin.HasValue) values[8] = Amount(margin.Value);
            Add(values, bold, bold);
        }
        // Python export_pdf.py prints three monetary subtotals for internal KP:
        // known purchase costs, full retail, and retail minus known purchase.
        // Do not treat a missing purchase price as a zero-priced input line.
        Total(positional ? "ИТОГО материалы" : "ИТОГО ЗА МАТЕРИАЛ:",
            internalPrices ? printedMaterialTotal : document.Totals.Materials,
            positional ? document.Totals.Smr : null, true,
            purchase: internalPrices ? printedPurchaseTotal : null,
            margin: internalPrices ? printedMaterialTotal - printedPurchaseTotal : null);
        if (document.Totals.Transport != 0m)
            Total("ТРАНСПОРТНЫЕ РАСХОДЫ:", document.Totals.Transport);
        if (document.Totals.Pnr != 0m)
            Total("ПУСКО-НАЛАДОЧНЫЕ РАБОТЫ:", document.Totals.Pnr);
        Total("СМР:", document.Totals.Smr);
        if (document.VatTop)
        {
            Total("ИТОГО без НДС:", document.Totals.Net);
            Total("НДС " + Rate(document.VatRate) + "%:", document.Totals.Vat);
            Total("ИТОГО с НДС:", document.Totals.Total, bold: true);
        }
        else
        {
            var totals = document.Totals;
            Total("ИТОГО с учётом НДС " + Rate(document.VatRate) + "%:",
                totals.Total + totals.DiscountAmount, bold: true);
            if (totals.DiscountPercent != 0m)
            {
                Total("Скидка " + totals.DiscountPercent.ToString("0.################", Ru) + "%:", -totals.DiscountAmount);
                Total("ИТОГО С УЧЁТОМ СКИДКИ:", totals.Total, bold: true);
            }
            if (vatLine) Total("в т.ч. НДС:", document.Totals.Vat);
        }
        if (y > bottom - 78) NewPage();
        y += 19;
        gfx!.DrawString("Подрядчик: _____________________ " +
            string.Join(" ", new[] { requisites?.CompanySignerPost, requisites?.CompanyName ?? document.Company,
                requisites?.CompanySignerName }.Where(x => !string.IsNullOrWhiteSpace(x))), Normal,
            XBrushes.Black, new XRect(left, y, widths.Sum(), 15), XStringFormats.TopLeft);
        y += 27;
        gfx.DrawString("Заказчик: _____________________ " +
            string.Join(" ", new[] { requisites?.CustomerPost, requisites?.Customer,
                requisites?.CustomerName }.Where(x => !string.IsNullOrWhiteSpace(x))), Normal,
            XBrushes.Black, new XRect(left, y, widths.Sum(), 15), XStringFormats.TopLeft);
        gfx.Dispose();
        using var stream = new MemoryStream();
        pdf.Save(stream, false);
        logo?.Dispose();
        logoStream?.Dispose();
        return stream.ToArray();
    }

    private static string Amount(decimal value) => value.ToString("#,##0.00", Ru);
    private static string Quantity(decimal value) => value.ToString("0.###", Ru);
    private static string Rate(decimal value) => value.ToString("0.##", Ru);
}
