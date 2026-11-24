using ClosedXML.Excel;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Python export_request.py XLSX layout and formula parity.</summary>
public static class ProcurementRequestExcel
{
    private const string MoneyFormat = "#,##0.00 \"₽\"";

    public static XLWorkbook Create(
        ProcurementRequestPlanner.DocumentInfo document,
        string supplier, string number, DateTime when, string due, string note,
        string author, IReadOnlyList<ProcurementRequestPlanner.RequestRow> rows,
        bool withPrices)
    {
        if (rows.Count == 0) throw new InvalidOperationException("В заявке нет позиций.");
        var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Заявка");
        var headers = withPrices
            ? new[] { "№", "Наименование", "Ед. изм.", "Кол-во", "Цена", "Сумма", "Примечание" }
            : new[] { "№", "Наименование", "Ед. изм.", "Кол-во", "Примечание" };
        var widths = withPrices ? new[] { 6d, 62d, 9d, 11d, 14d, 16d, 28d }
                                : new[] { 6d, 62d, 9d, 11d, 28d };
        var last = headers.Length;
        for (var col = 0; col < widths.Length; col++)
            ws.Column(col + 1).Width = widths[col];
        ws.Style.Font.FontName = "Calibri";
        ws.Style.Font.FontSize = 11;
        ws.ShowGridLines = false;

        void MergedLine(int line, string text, double size = 11d, bool bold = false,
            XLAlignmentHorizontalValues align = XLAlignmentHorizontalValues.Left)
        {
            var range = ws.Range(line, 1, line, last);
            range.Merge();
            range.FirstCell().Value = text ?? "";
            range.Style.Font.FontSize = size;
            range.Style.Font.Bold = bold;
            range.Style.Alignment.Horizontal = align;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            range.Style.Alignment.WrapText = true;
        }
        var r = 1;
        foreach (var companyLine in (document.CompanyHeader ?? "")
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            MergedLine(r++, companyLine.Trim(), 10, bold: true,
                XLAlignmentHorizontalValues.Right);
        if (r > 1) r++;
        MergedLine(r++, $"ЗАЯВКА № {number} от {when:dd.MM.yyyy}", 14, true,
            XLAlignmentHorizontalValues.Center);
        ws.Row(r - 1).Height = 22;
        r++;
        MergedLine(r++, "Поставщик: " + supplier, 12, true);
        MergedLine(r++, "Объект: " + document.ObjectName +
            (string.IsNullOrWhiteSpace(document.Address) ? "" : ", " + document.Address));
        if (!string.IsNullOrWhiteSpace(due)) MergedLine(r++, "Срок поставки: " + due);
        if (!string.IsNullOrWhiteSpace(note))
        {
            MergedLine(r, note);
            ws.Row(r++).Height = 15 * Math.Max(1, Math.Min(8, note.Length / 110 + 1));
        }
        r++;
        var headerRow = r;
        for (var c = 1; c <= headers.Length; c++)
        {
            var cell = ws.Cell(r, c);
            cell.Value = headers[c - 1];
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            cell.Style.Alignment.WrapText = true;
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        }
        ws.Row(r).Height = 28;
        r++;
        var firstDataRow = r;
        foreach (var (item, index) in rows.Select((row, i) => (row, i + 1)))
        {
            ws.Cell(r, 1).Value = index;
            ws.Cell(r, 2).Value = item.Name;
            ws.Cell(r, 3).Value = item.Unit;
            ws.Cell(r, 4).Value = item.Quantity;
            if (withPrices)
            {
                if (item.UnitPrice is not null)
                {
                    ws.Cell(r, 5).Value = item.UnitPrice.Value;
                    ws.Cell(r, 6).FormulaA1 = $"D{r}*E{r}";
                }
                ws.Cell(r, 7).Value = item.Note;
                foreach (var c in new[] { 5, 6 })
                {
                    ws.Cell(r, c).Style.NumberFormat.Format = MoneyFormat;
                    ws.Cell(r, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                }
            }
            else ws.Cell(r, 5).Value = item.Note;
            for (var c = 1; c <= last; c++)
            {
                var cell = ws.Cell(r, c);
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                cell.Style.Alignment.WrapText = true;
                if (c != 2 && c != last)
                    cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }
            if (item.Name.Length > 80)
                ws.Row(r).Height = 15 * Math.Min(5, item.Name.Length / 80 + 1);
            r++;
        }
        if (withPrices)
        {
            ws.Range(r, 1, r, 5).Merge();
            ws.Cell(r, 1).Value = "ИТОГО:";
            ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 6).FormulaA1 = $"SUM(F{firstDataRow}:F{r - 1})";
            ws.Cell(r, 6).Style.NumberFormat.Format = MoneyFormat;
            ws.Cell(r, 6).Style.Font.Bold = true;
            for (var c = 1; c <= last; c++)
                ws.Cell(r, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            r++;
        }
        r += 2;
        MergedLine(r++, $"Всего позиций: {rows.Count}");
        r += 2;
        MergedLine(r, "Заявку составил: ____________________  " + author);
        ws.PageSetup.PageOrientation = XLPageOrientation.Portrait;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.PrintAreas.Add($"A1:{(char)('A' + last - 1)}{r}");
        return workbook;
    }
}
