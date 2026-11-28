using System.IO;
using System.Windows;
using ClosedXML.Excel;
using GmcSmeta.Core;

namespace GmcSmeta.App;

/// <summary>
/// Общий xlsx-экспорт (этап 9): диалог сохранения, книга с одним листом,
/// после сохранения файл открывается. Все выгрузки окна идут через него.
/// </summary>
public static class XlsxExport
{
    /// <summary>Existing one-sheet export (unchanged API).</summary>
    public static bool Save(Window? owner, string suggestedName, Action<IXLWorksheet> fill) =>
        SaveWorkbook(owner, suggestedName,
            workbook => fill(workbook.Worksheets.Add("Лист1")));

    /// <summary>
    /// Save a complete multi-sheet workbook for a KS-2 + KS-3 package.
    /// One file with individual acts and a combined KS-3 sheet.
    /// </summary>
    public static bool SaveWorkbook(Window? owner, string suggestedName,
        Action<XLWorkbook> fill, bool autoFit = true)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить в Excel",
            Filter = "Excel (*.xlsx)|*.xlsx",
            FileName = SanitizeFileName(suggestedName),
        };
        if (owner is not null && dialog.ShowDialog(owner) != true) return false;
        if (owner is null && dialog.ShowDialog() != true) return false;
        try
        {
            using var workbook = new XLWorkbook();
            fill(workbook);
            if (workbook.Worksheets.Count == 0)
                throw new InvalidOperationException("В книге не создан ни один лист.");
            if (autoFit)
                foreach (var sheet in workbook.Worksheets)
                    sheet.Columns().AdjustToContents(
                        1, Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 1, 200));
            ExportFilePublication.Write(dialog.FileName, stream => workbook.SaveAs(stream));
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Экспорт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName)
            { UseShellExecute = true });
        }
        catch
        {
            // The saved workbook is valid even if no Excel viewer is installed.
        }
        return true;
    }

    /// <summary>Save a preformatted workbook, including embedded customer XLSX templates.</summary>
    public static bool SaveCreatedWorkbook(Window? owner, string suggestedName,
        Func<XLWorkbook> create)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить в Excel",
            Filter = "Excel (*.xlsx)|*.xlsx",
            FileName = SanitizeFileName(suggestedName)
        };
        if (owner is not null && dialog.ShowDialog(owner) != true) return false;
        if (owner is null && dialog.ShowDialog() != true) return false;
        try
        {
            using var workbook = create();
            if (workbook.Worksheets.Count == 0)
                throw new InvalidOperationException("В книге нет листов.");
            ExportFilePublication.Write(dialog.FileName, stream => workbook.SaveAs(stream));
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Экспорт", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dialog.FileName)
            { UseShellExecute = true });
        }
        catch { /* Excel viewer is optional. */ }
        return true;
    }

    internal static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, ' ');
        while (name.Contains("  ")) name = name.Replace("  ", " ");
        return name.Trim();
    }

    /// <summary>Заголовок таблицы: жирный, серый фон, перенос.</summary>
    public static void Header(IXLWorksheet sheet, int row, IEnumerable<string> cells)
    {
        var i = 1;
        foreach (var text in cells)
        {
            var cell = sheet.Cell(row, i);
            cell.Value = text;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E2E8F0");
            cell.Style.Alignment.WrapText = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            i++;
        }
    }
}
