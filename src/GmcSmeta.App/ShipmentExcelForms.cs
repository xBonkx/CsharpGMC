using ClosedXML.Excel;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Excel issue list following Python export_ship_excel: quantities, receipt check,
/// notes, grouped systems, issuer and receiver. No financial columns on this form.</summary>
public static class ShipmentExcelForms
{
    public static XLWorkbook Create(SqlServerShipmentPrintData.Snapshot shipment)
    {
        ArgumentNullException.ThrowIfNull(shipment);
        var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Ведомость");
        var widths = new[] { 6d, 62d, 8d, 10d, 12d, 22d };
        for (var i = 0; i < widths.Length; i++) sheet.Column(i + 1).Width = widths[i];

        var date = shipment.Date == DateTime.MinValue ? "" : " от " + shipment.Date.ToString("dd.MM.yyyy");
        sheet.Cell("A1").Value = "Ведомость выдачи № " + shipment.Number + date;
        sheet.Cell("A1").Style.Font.Bold = true;
        sheet.Cell("A1").Style.Font.FontSize = 14;
        sheet.Range("A1:F1").Merge();
        sheet.Cell("A2").Value = "Объект: " + shipment.ObjectName +
            (shipment.Recipient.Length == 0 ? "" : "   ·   Получатель: " + shipment.Recipient);
        sheet.Range("A2:F2").Merge();
        sheet.Row(1).Height = 26;
        sheet.Row(2).Height = 24;

        string[] columns = { "№", "Наименование", "Ед.", "Кол-во", "Получено", "Примечание" };
        for (var col = 1; col <= columns.Length; col++)
        {
            var cell = sheet.Cell(4, col);
            cell.Value = columns[col - 1];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E2E8F0");
            cell.Style.Alignment.WrapText = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }
        Box(sheet, 4);
        sheet.Row(4).Height = 27;

        var row = 5;
        var index = 1;
        var group = "";
        var multiDocs = shipment.Lines.Select(x => x.Document).Distinct().Skip(1).Any();
        foreach (var line in shipment.Lines)
        {
            var current = line.Document + "\u001f" + line.System;
            if (current != group)
            {
                group = current;
                var title = multiDocs ? line.Document + " · " + line.System : line.System;
                sheet.Cell(row, 2).Value = title;
                sheet.Cell(row, 2).Style.Font.Bold = true;
                sheet.Range(row, 1, row, 6).Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF2F7");
                sheet.Row(row).Height = 21;
                Box(sheet, row++);
            }
            sheet.Cell(row, 1).Value = index++;
            sheet.Cell(row, 2).Value = line.Name;
            sheet.Cell(row, 3).Value = line.Unit;
            sheet.Cell(row, 4).Value = (double)line.Quantity;
            sheet.Cell(row, 4).Style.NumberFormat.Format = "0.###";
            sheet.Cell(row, 6).Value = line.Note;
            sheet.Range(row, 1, row, 6).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            sheet.Range(row, 1, row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            sheet.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            sheet.Cell(row, 2).Style.Alignment.WrapText = true;
            sheet.Cell(row, 6).Style.Alignment.WrapText = true;
            sheet.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            if (line.IsSub) sheet.Cell(row, 2).Style.Font.Italic = true;
            sheet.Row(row).Height = Math.Min(85, Math.Max(18, Math.Ceiling(line.Name.Length / 60d) * 15));
            Box(sheet, row++);
        }

        row += 2;
        sheet.Cell(row, 2).Value = "Выдал: ____________________ " + shipment.IssuedBy;
        sheet.Cell(row + 2, 2).Value = "Получил: ____________________ " + shipment.Recipient;
        if (shipment.Note.Length > 0)
            sheet.Cell(row + 4, 2).Value = "Примечание: " + shipment.Note;

        sheet.PageSetup.PageOrientation = XLPageOrientation.Portrait;
        sheet.PageSetup.FitToPages(1, 0);
        sheet.PageSetup.SetRowsToRepeatAtTop(4, 4);
        sheet.PageSetup.PrintAreas.Clear();
        sheet.PageSetup.PrintAreas.Add("A1:F" + (row + (shipment.Note.Length > 0 ? 4 : 2)));
        sheet.SheetView.FreezeRows(4);
        return book;
    }

    private static void Box(IXLWorksheet sheet, int row)
    {
        var cells = sheet.Range(row, 1, row, 6);
        cells.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        cells.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
    }
}
