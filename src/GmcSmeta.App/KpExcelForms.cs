using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using GmcSmeta.Core.Domain;
using GmcSmeta.Core.Db;
using System.Windows.Media.Imaging;

namespace GmcSmeta.App;

/// <summary>
/// Python export_kp.py / export_kp_pos.py print layouts, projected from the
/// authoritative SQL Server EstimateDocument. No mutation of document rows.
/// </summary>
public static class KpExcelForms
{
    private const string MoneyFormat = "#,##0.00 \"₽\"";
    private static readonly XLColor BorderColor = XLColor.Black;
    private static readonly XLColor SubColor = XLColor.FromHtml("#E8F5FC");
    private static readonly XLColor SubPosition = XLColor.FromHtml("#F3F4F6");

    public static XLWorkbook Create(EstimateDocument document, DocRow header,
        bool internalPrices = false, bool positional = false,
        IReadOnlyDictionary<int, decimal>? smrBySystem = null, bool vatLine = false,
        SqlServerKpRequisites.Values? requisites = null, decimal? smrPercent = null,
        bool manualNumbering = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(header);
        if (document.ContractId != header.Id)
            throw new InvalidOperationException("Несовпадение документа и выгрузки.");
        if (positional && internalPrices)
            throw new ArgumentException("Позиционный КП и внутренние цены — отдельные формы.");
        if (document.SmrMode == "percent" && !positional && smrPercent is null)
            throw new InvalidOperationException("Для СМР в процентах требуется сохранённый процент договора.");
        if (document.SmrMode == "systems" && !positional && smrBySystem is null)
            throw new InvalidOperationException("Для СМР по системам требуется снимок сумм систем из SQL Server.");
        return positional
            ? CreatePositional(document, header, vatLine, requisites)
            : CreateRegular(document, header, internalPrices, smrBySystem, vatLine, requisites, smrPercent,
                manualNumbering);
    }

    private static XLWorkbook CreateRegular(EstimateDocument doc, DocRow header,
        bool internalPrices, IReadOnlyDictionary<int, decimal>? smrBySystem, bool vatLine,
        SqlServerKpRequisites.Values? requisites, decimal? smrPercent,
        bool manualNumbering)
    {
        var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("КП");
        double[] widths = internalPrices
            ? new double[] { 6, 52, 8, 10, 15, 17, 15, 17, 15 }
            : new double[] { 6, 52, 8, 10, 15, 17 };
        var lastCol = widths.Length;
        for (var i = 0; i < widths.Length; i++) sheet.Column(i + 1).Width = widths[i];
        var last = XLHelper.GetColumnLetterFromNumber(lastCol);
        var price = internalPrices ? 7 : 5;
        var total = internalPrices ? 8 : 6;
        var totalLetter = XLHelper.GetColumnLetterFromNumber(total);
        var row = WriteTitle(sheet, doc, header, lastCol, start: 2, requisites);
        var headers = internalPrices
            ? new[] { "Поз.", "Наименование и техническая характеристика", "Ед. изм.", "Коли-чество", "Цена", "Всего", "Цена", "Всего", "Маржа" }
            : new[] { "Поз.", "Наименование и техническая характеристика", "Ед. изм.", "Коли-чество", "Цена", "Всего" };
        var headerRow = row;
        for (var col = 1; col <= headers.Length; col++)
        {
            sheet.Cell(row, col).Value = headers[col - 1];
            sheet.Cell(row, col).Style.Font.Bold = true;
        }
        SetRow(sheet, row, lastCol, 31.5);
        row++;
        if (internalPrices)
        {
            var sub = new[] { "", "", "", "", "закуп", "закуп", "розница", "розница", "розн. − закуп" };
            for (var col = 1; col <= sub.Length; col++)
            {
                sheet.Cell(row, col).Value = sub[col - 1];
                sheet.Cell(row, col).Style.Font.Italic = true;
                sheet.Cell(row, col).Style.Font.FontSize = 10;
            }
            SetRow(sheet, row, lastCol, 21);
            row++;
        }

        var firstItem = row;
        var systemMaterialRows = new List<int>();
        var systemSmrRows = new List<int>();
        var activeSystemId = 0;
        var activeFirst = row;
        var bySystem = doc.SmrMode == "systems";
        var hiddenSubParents = doc.Rows.Where(x => x.IsAreaSummary).Select(x => x.Id).ToHashSet();
        var excludedParents = doc.Rows.Where(x => !x.IsSystem && x.OutOfKp)
            .Select(x => x.Id).ToHashSet();
        var skippedSystems = HiddenSystems(doc.Rows, hiddenSubParents, excludedParents);
        var nextNumber = 1;

        int Total(string label, string? formula = null, decimal? amount = null,
            bool bold = true, IReadOnlyDictionary<int, string>? otherFormulas = null)
        {
            var current = row++;
            sheet.Cell(current, 1).Value = nextNumber++;
            sheet.Cell(current, 2).Value = label;
            sheet.Range(current, 2, current, internalPrices ? 4 : total - 1).Merge();
            sheet.Cell(current, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            sheet.Cell(current, 2).Style.Font.Bold = bold;
            if (formula is not null) sheet.Cell(current, total).FormulaA1 = formula;
            else if (amount.HasValue) sheet.Cell(current, total).Value = (double)amount.Value;
            if (otherFormulas is not null)
                foreach (var pair in otherFormulas)
                    sheet.Cell(current, pair.Key).FormulaA1 = pair.Value;
            for (var col = 5; col <= lastCol; col++)
                sheet.Cell(current, col).Style.NumberFormat.Format = MoneyFormat;
            sheet.Cell(current, total).Style.Font.Bold = bold;
            SetRow(sheet, current, lastCol, 21);
            return current;
        }

        void FinishSystem()
        {
            if (!bySystem || activeSystemId == 0) return;
            var hasPositions = row > activeFirst;
            var perSystem = smrBySystem!.GetValueOrDefault(activeSystemId);
            if (!hasPositions && perSystem == 0m) return;
            if (hasPositions)
            {
                var mat = Total("МАТЕРИАЛ:", "SUM(" + totalLetter + activeFirst + ":" +
                    totalLetter + (row - 1) + ")", bold: false);
                systemMaterialRows.Add(mat);
                var smr = Total("СМР:", amount: perSystem, bold: false);
                systemSmrRows.Add(smr);
                Total("ИТОГО:", totalLetter + mat + "+" + totalLetter + smr);
            }
            else
            {
                systemSmrRows.Add(Total("СМР:", amount: perSystem, bold: false));
            }
        }

        foreach (var source in doc.Rows)
        {
            if (source.IsSystem)
            {
                FinishSystem();
                activeSystemId = skippedSystems.Contains(source.SystemId) ? 0 : source.SystemId;
                if (activeSystemId == 0) continue;
                if (!string.Equals(source.Name, "Без системы", StringComparison.Ordinal))
                {
                    sheet.Cell(row, 1).Value = manualNumbering ? source.Num : nextNumber.ToString();
                    nextNumber++;
                    sheet.Cell(row, 2).Value = source.Name;
                    sheet.Cell(row, 2).Style.Font.Bold = true;
                    sheet.Cell(row, 2).Style.Font.Italic = source.ParentSystemId.HasValue;
                    for (var col = 3; col <= lastCol; col++)
                        sheet.Cell(row, col).Value = "*";
                    SetRow(sheet, row, lastCol, 23);
                    if (source.ParentSystemId.HasValue)
                        sheet.Range(row, 1, row, lastCol).Style.Fill.BackgroundColor = SubColor;
                    row++;
                }
                activeFirst = row;
                continue;
            }
            if (source.OutOfKp || (source.ParentPositionId.HasValue &&
                (hiddenSubParents.Contains(source.ParentPositionId.Value) ||
                 excludedParents.Contains(source.ParentPositionId.Value)))) continue;
            if (manualNumbering)
                sheet.Cell(row, 1).Value = source.Num;
            else if (!source.IsSub)
                sheet.Cell(row, 1).Value = nextNumber++;
            else sheet.Cell(row, 1).Value = "";
            sheet.Cell(row, 2).Value = source.Name;
            sheet.Cell(row, 3).Value = source.Unit;
            sheet.Cell(row, 4).Value = (double)source.Qty;
            if (!source.IsSub)
            {
                sheet.Cell(row, price).Value = (double)source.Price;
                sheet.Cell(row, total).FormulaA1 = XLHelper.GetColumnLetterFromNumber(price) + row + "*D" + row;
                if (internalPrices)
                {
                    if (source.PurchasePrice.HasValue)
                    {
                        sheet.Cell(row, 5).Value = (double)source.PurchasePrice.Value;
                        sheet.Cell(row, 6).FormulaA1 = "E" + row + "*D" + row;
                    }
                    sheet.Cell(row, 9).FormulaA1 = "H" + row + "-F" + row;
                }
            }
            SetRow(sheet, row, lastCol, Math.Min(94.5, Math.Max(19, Math.Ceiling(source.Name.Length / 50d) * 15.75)));
            sheet.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            if (source.IsSub)
            {
                sheet.Range(row, 1, row, lastCol).Style.Fill.BackgroundColor = SubPosition;
                sheet.Range(row, 1, row, lastCol).Style.Font.Italic = true;
            }
            sheet.Cell(row, 4).Style.NumberFormat.Format = "General";
            for (var col = 5; col <= lastCol; col++)
                sheet.Cell(row, col).Style.NumberFormat.Format = MoneyFormat;
            row++;
        }
        FinishSystem();
        var lastItem = row - 1;

        string MaterialTotal(int col)
        {
            if (bySystem && col == total && systemMaterialRows.Count > 0)
                return "SUM(" + string.Join(",", systemMaterialRows.Select(x => totalLetter + x)) + ")";
            var letter = XLHelper.GetColumnLetterFromNumber(col);
            return lastItem >= firstItem ? "SUM(" + letter + firstItem + ":" + letter + lastItem + ")" : "0";
        }

        var companion = internalPrices
            ? new Dictionary<int, string> { [6] = MaterialTotal(6), [9] = MaterialTotal(9) }
            : null;
        var materialRow = Total("ИТОГО ЗА МАТЕРИАЛ:", MaterialTotal(total), otherFormulas: companion);
        var components = new List<int> { materialRow };
        if (doc.Totals.Transport != 0m)
            components.Add(Total("ТРАНСПОРТНЫЕ РАСХОДЫ:", amount: doc.Totals.Transport));
        if (doc.Totals.Pnr != 0m)
            components.Add(Total("ПУСКО-НАЛАДОЧНЫЕ РАБОТЫ:", amount: doc.Totals.Pnr));
        var smrValue = bySystem
            ? (systemSmrRows.Count > 0 ? "SUM(" +
                string.Join(",", systemSmrRows.Select(x => totalLetter + x)) + ")" : "0")
            : "";
        var smrRow = bySystem ? Total("ИТОГО СМР:", smrValue)
            : doc.SmrMode == "percent"
                ? Total("СМР:", "ROUND(" + totalLetter + materialRow + "*" +
                    Rate(smrPercent!.Value) + "/100,2)")
                : Total("СМР:", amount: doc.Totals.Smr);
        components.Add(smrRow);
        var sum = string.Join("+", components.Select(x => totalLetter + x));
        if (doc.VatTop)
        {
            var net = Total("ИТОГО без НДС:", sum);
            var vat = Total("НДС " + Rate(doc.VatRate) + "%:",
                "ROUND(" + totalLetter + net + "*" + Rate(doc.VatRate) + "/100,2)", bold: false);
            Total("ИТОГО с НДС:", totalLetter + net + "+" + totalLetter + vat);
        }
        else
        {
            var gross = Total("ИТОГО, с учетом НДС " + Rate(doc.VatRate) + "%:", sum);
            if (doc.Totals.DiscountPercent != 0m)
            {
                var pct = doc.Totals.DiscountPercent.ToString("0.################", CultureInfo.InvariantCulture);
                var discount = Total("Скидка " + pct + "%:",
                    "-ROUND(ROUND(" + totalLetter + gross + ",2)*" + pct + "/100,2)", bold: false);
                gross = Total("ИТОГО С УЧЁТОМ СКИДКИ:",
                    "ROUND(" + totalLetter + gross + "+" + totalLetter + discount + ",2)");
            }
            if (vatLine)
                Total("в т.ч. НДС " + Rate(doc.VatRate) + "%:",
                    "ROUND(" + totalLetter + gross + "*" + Rate(doc.VatRate) + "/(100+" +
                    Rate(doc.VatRate) + "),2)", bold: false);
        }

        sheet.PageSetup.PageOrientation = internalPrices
            ? XLPageOrientation.Landscape : XLPageOrientation.Portrait;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.PrintAreas.Add("A1:" + last + (row - 1));
        sheet.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);
        sheet.SheetView.FreezeRows(headerRow + (internalPrices ? 1 : 0));
        return book;
    }

    private static XLWorkbook CreatePositional(EstimateDocument doc, DocRow header, bool vatLine,
        SqlServerKpRequisites.Values? requisites)
    {
        var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Материалы и работы");
        double[] widths = { 6.5, 60, 10.3, 11, 17, 19, 17, 19 };
        for (var i = 0; i < widths.Length; i++) sheet.Column(i + 1).Width = widths[i];
        var row = WriteTitle(sheet, doc, header, 8, 1, requisites);
        var h1 = row;
        for (var col = 1; col <= 4; col++)
        {
            sheet.Range(h1, col, h1 + 1, col).Merge();
            sheet.Cell(h1, col).Value = new[] { "Поз.", "Наименование и техническая характеристика", "Ед. изм.", "Коли-чество" }[col - 1];
        }
        sheet.Range(h1, 5, h1, 6).Merge();
        sheet.Range(h1, 7, h1, 8).Merge();
        sheet.Cell(h1, 5).Value = "Стоимость материалов";
        sheet.Cell(h1, 7).Value = "Стоимость работ";
        foreach (var (col, name) in new[] { (5, "Стоимость за ед. изм"), (6, "Всего"),
                     (7, "Стоимость за ед. изм"), (8, "Всего") })
            sheet.Cell(h1 + 1, col).Value = name;
        SetRow(sheet, h1, 8, 22);
        SetRow(sheet, h1 + 1, 8, 30);
        sheet.Range(h1, 1, h1 + 1, 8).Style.Font.Bold = true;
        row = h1 + 2;
        var first = row;
        var index = 1;
        var areaRoots = doc.Rows.Where(x => x.IsAreaSummary).Select(x => x.Id).ToHashSet();
        var excludedParents = doc.Rows.Where(x => !x.IsSystem && x.OutOfKp)
            .Select(x => x.Id).ToHashSet();
        var skippedSystems = HiddenSystems(doc.Rows, areaRoots, excludedParents);
        foreach (var source in doc.Rows)
        {
            if (source.IsSystem)
            {
                if (source.Name == "Без системы" || skippedSystems.Contains(source.SystemId)) continue;
                sheet.Cell(row, 1).Value = index++;
                sheet.Cell(row, 2).Value = source.Name;
                sheet.Range(row, 1, row, 8).Style.Fill.BackgroundColor = source.ParentSystemId.HasValue
                    ? XLColor.FromHtml("#E2F0D9") : XLColor.FromHtml("#92D050");
                sheet.Cell(row, 2).Style.Font.Bold = true;
                SetRow(sheet, row++, 8, 24);
                continue;
            }
            if (source.OutOfKp || (source.ParentPositionId.HasValue &&
                (areaRoots.Contains(source.ParentPositionId.Value) ||
                 excludedParents.Contains(source.ParentPositionId.Value)))) continue;
            if (!source.IsSub) sheet.Cell(row, 1).Value = index++;
            sheet.Cell(row, 2).Value = source.Name;
            sheet.Cell(row, 3).Value = source.Unit;
            sheet.Cell(row, 4).Value = (double)source.Qty;
            if (!source.IsSub)
            {
                sheet.Cell(row, 5).Value = (double)source.Price;
                sheet.Cell(row, 6).FormulaA1 = "D" + row + "*E" + row;
                sheet.Cell(row, 7).Value = (double)(source.WorkPrice ?? 0m);
                sheet.Cell(row, 8).FormulaA1 = "G" + row + "*D" + row;
            }
            SetRow(sheet, row, 8, Math.Min(75, Math.Max(19, Math.Ceiling(source.Name.Length / 70d) * 15)));
            sheet.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            sheet.Cell(row, 2).Style.Font.Italic = source.IsSub;
            for (var col = 5; col <= 8; col++)
                sheet.Cell(row, col).Style.NumberFormat.Format = MoneyFormat;
            row++;
        }
        var last = row - 1;
        int Total(string label, string? f = null, decimal? value = null, string? work = null, bool bold = true)
        {
            var here = row++;
            sheet.Cell(here, 1).Value = index++;
            sheet.Range(here, 2, here, 5).Merge();
            sheet.Cell(here, 2).Value = label;
            sheet.Cell(here, 2).Style.Font.Bold = bold;
            if (f is not null) sheet.Cell(here, 6).FormulaA1 = f;
            else if (value.HasValue) sheet.Cell(here, 6).Value = (double)value.Value;
            if (work is not null) sheet.Cell(here, 8).FormulaA1 = work;
            sheet.Cell(here, 6).Style.NumberFormat.Format = MoneyFormat;
            sheet.Cell(here, 8).Style.NumberFormat.Format = MoneyFormat;
            sheet.Cell(here, 6).Style.Font.Bold = bold;
            SetRow(sheet, here, 8, 22);
            return here;
        }
        var mat = Total("ИТОГО материалы с НДС " + Rate(doc.VatRate) + "%",
            last >= first ? "SUM(F" + first + ":F" + last + ")" : "0",
            work: last >= first ? "SUM(H" + first + ":H" + last + ")" : "0");
        var parts = new List<int> { mat };
        if (doc.Totals.Transport != 0m)
            parts.Add(Total("Транспортные расходы", value: doc.Totals.Transport));
        if (doc.Totals.Pnr != 0m)
            parts.Add(Total("Пуско-наладочные работы", value: doc.Totals.Pnr));
        parts.Add(Total("ИТОГО СМР с НДС " + Rate(doc.VatRate) + "%", "H" + mat));
        var sum = string.Join("+", parts.Select(x => "F" + x));
        if (doc.VatTop)
        {
            var net = Total("ИТОГО без НДС", sum);
            var vat = Total("НДС " + Rate(doc.VatRate) + "%",
                "ROUND(F" + net + "*" + Rate(doc.VatRate) + "/100,2)", bold: false);
            Total("ИТОГО с НДС", "F" + net + "+F" + vat);
        }
        else
        {
            var gross = Total("ВСЕГО С УЧЕТОМ МАТЕРИАЛОВ И СМР С НДС " + Rate(doc.VatRate) + "%", sum);
            if (doc.Totals.DiscountPercent != 0m)
            {
                var pct = doc.Totals.DiscountPercent.ToString("0.################", CultureInfo.InvariantCulture);
                var discount = Total("Скидка " + pct + "%", "-ROUND(ROUND(F" + gross + ",2)*" + pct + "/100,2)", bold: false);
                gross = Total("ИТОГО С УЧЁТОМ СКИДКИ", "ROUND(F" + gross + "+F" + discount + ",2)");
            }
            if (vatLine)
                Total("в т.ч. НДС " + Rate(doc.VatRate) + "%",
                    "ROUND(F" + gross + "*" + Rate(doc.VatRate) + "/(100+" + Rate(doc.VatRate) + "),2)",
                    bold: false);
        }
        row += 2;
        sheet.Cell(row, 2).Value = "Подрядчик " +
            string.Join(" ", new[] { requisites?.CompanySignerPost, requisites?.CompanyName ?? doc.Company,
                requisites?.CompanySignerName }.Where(x => !string.IsNullOrWhiteSpace(x))) +
            " _______________________";
        sheet.Cell(row + 4, 2).Value = "Заказчик " +
            string.Join(" ", new[] { requisites?.CustomerPost, requisites?.Customer,
                requisites?.CustomerName }.Where(x => !string.IsNullOrWhiteSpace(x))) +
            " _______________________";
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(h1, h1 + 1);
        sheet.PageSetup.PrintAreas.Add("A1:H" + (row + 4));
        sheet.SheetView.FreezeRows(h1 + 1);
        return book;
    }

    private static int WriteTitle(IXLWorksheet sheet, EstimateDocument doc,
        DocRow header, int lastColumn, int start, SqlServerKpRequisites.Values? requisites)
    {
        var row = start;
        if (requisites is { CompanyHeader.Count: > 0 })
        {
            row = 2;
            if (requisites.CompanyLogo is { Length: > 16 } bytes)
            {
                using var logoStream = new MemoryStream(bytes, writable: false);
                var decoder = BitmapDecoder.Create(logoStream,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var picture = decoder.Frames[0];
                var width = Math.Max(1, (int)Math.Round(78d * picture.PixelWidth / picture.PixelHeight));
                logoStream.Position = 0;
                sheet.AddPicture(logoStream).MoveTo(sheet.Cell("A2")).WithSize(width, 78);
            }
            foreach (var line in requisites.CompanyHeader)
            {
                sheet.Range(row, 4, row, lastColumn).Merge();
                var cell = sheet.Cell(row, 4);
                cell.Value = line;
                cell.Style.Font.FontName = "Arial";
                cell.Style.Font.FontSize = 10;
                cell.Style.Font.Bold = true;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                sheet.Row(row++).Height = 15;
            }
            row = Math.Max(row + 2, 6);
        }
        var title = header.Kind switch
        {
            "main" => "Приложение № 1 к Договору подряда",
            "dop" => "Приложение № 1 к Дополнительному соглашению",
            _ => "Коммерческое предложение"
        };
        if (header.Kind != "kp")
        {
            if (header.Number.Length > 0) title += " № " + header.Number;
            if (header.Date.Length > 0) title += " от " + header.Date + " г.";
        }
        sheet.Range(row, 1, row, lastColumn).Merge();
        sheet.Cell(row, 1).Value = title;
        sheet.Cell(row, 1).Style.Font.Bold = true;
        sheet.Cell(row, 1).Style.Font.FontSize = 14;
        sheet.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        sheet.Row(row++).Height = 24;
        if (header.Kind == "dop" && requisites?.BaseContractNumber.Length > 0)
        {
            sheet.Range(row, 1, row, lastColumn).Merge();
            sheet.Cell(row, 1).Value = "к Договору подряда № " + requisites.BaseContractNumber +
                (requisites.BaseContractDate.Length > 0 ? " от " + requisites.BaseContractDate + " г." : "");
            sheet.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Row(row++).Height = 20;
        }
        sheet.Range(row, 1, row, lastColumn).Merge();
        sheet.Cell(row, 1).Value = doc.ObjectName + (header.Name.Length > 0 ? " — " + header.Name : "");
        sheet.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        sheet.Row(row++).Height = 23;
        if (doc.Company.Length > 0 && requisites is null)
        {
            sheet.Range(row, 1, row, lastColumn).Merge();
            sheet.Cell(row, 1).Value = doc.Company;
            sheet.Cell(row, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            sheet.Row(row++).Height = 21;
        }
        return row + 1;
    }

    /// <summary>Do not print system headers whose only positions are excluded or folded.</summary>
    internal static HashSet<int> HiddenSystems(IReadOnlyList<EstimateRow> rows,
        ISet<int> areaRoots, ISet<int> excludedParents)
    {
        return rows.Where(x => !x.IsSystem).GroupBy(x => x.SystemId)
            .Where(group => !group.Any(position =>
                !position.OutOfKp &&
                (!position.ParentPositionId.HasValue ||
                 (!areaRoots.Contains(position.ParentPositionId.Value) &&
                  !excludedParents.Contains(position.ParentPositionId.Value)))))
            .Select(group => group.Key).ToHashSet();
    }

    private static void SetRow(IXLWorksheet sheet, int row, int lastCol, double height)
    {
        sheet.Row(row).Height = height;
        var range = sheet.Range(row, 1, row, lastCol);
        range.Style.Font.FontName = "Times New Roman";
        range.Style.Font.FontSize = 12;
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Alignment.WrapText = true;
        range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        range.Style.Border.OutsideBorderColor = BorderColor;
        range.Style.Border.InsideBorderColor = BorderColor;
    }

    private static string Rate(decimal value) =>
        value.ToString("0.################", CultureInfo.InvariantCulture);
}
