using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using ClosedXML.Excel;

namespace GmcSmeta.App;

public partial class EstimateView
{
    internal void SummaryExcel_Click(object sender, RoutedEventArgs e)
    {
        if (!SummarySnapshotReady ||
            SummaryGrid?.ItemsSource is not ObservableCollection<SummaryRow> rows)
        {
            SetStatus("Обновите сводную перед выгрузкой.", true);
            return;
        }
        // Capture the displayed report before the modal save dialog. A later
        // refresh cannot mix rows, totals or title from a different view.
        var snapshot = rows.ToArray();
        var title = "Сводная — " + (_estimate?.Title ?? _contractId.ToString());
        XlsxExport.Save(Window.GetWindow(this), title,
            sheet => SummaryDisplayedExcel.Fill(sheet, snapshot));
    }
}

/// <summary>Report of the current view, not an editable round-trip workbook.</summary>
internal static class SummaryDisplayedExcel
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    internal static void Fill(IXLWorksheet sheet, IReadOnlyList<EstimateView.SummaryRow> rows)
    {
        XlsxExport.Header(sheet, 1, new[]
        {
            "Наименование", "Ед.", "Кол-во",
            "Материалы: закуп за ед.", "Материалы: розн. за ед.",
            "Материалы: закуп", "Материалы: розница",
            "Работы: закуп за ед.", "Работы: розн. за ед.",
            "Работы: закуп", "Работы: розница",
            "Итого: закуп", "Итого: розница",
            "Маржа материалов, ₽", "Маржа работ, ₽", "СМР: наценка, %",
        });
        var index = 2;
        foreach (var r in rows)
        {
            sheet.Cell(index, 1).Value = r.Name;
            sheet.Cell(index, 2).Value = r.Unit;
            var values = new[]
            {
                r.QtyText, r.CostUnitText, r.PriceUnitText, r.MCostText, r.MSellText,
                r.WorkCostText, r.WorkSellText, r.WCostText, r.WSellText,
                r.TotalCostText, r.TotalSellText, r.MatMarginText, r.WorkMarginText, r.MarkupText
            };
            for (int i = 0; i < values.Length; i++)
                WriteDisplayedNumber(sheet.Cell(index, i + 3), values[i], i == 0);
            if (r.Kind is "system" or "total")
                sheet.Row(index).Style.Font.Bold = true;
            index++;
        }
        sheet.SheetView.FreezeRows(1);
    }

    private static void WriteDisplayedNumber(IXLCell cell, string text, bool quantity)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        // Parse only the already formatted Russian display numbers, never
        // evaluate formulas or turn unknown markers into zero.
        var normalized = text.Replace("\u00a0", "").Replace("\u202f", "")
            .Replace(" ", "").Replace('−', '-');
        if (decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                Ru, out var value))
        {
            cell.Value = (double)value;
            cell.Style.NumberFormat.Format = quantity
                ? (value == decimal.Truncate(value) ? "#,##0" : "#,##0.###")
                : "#,##0.00";
        }
        else cell.Value = text;
    }
}
