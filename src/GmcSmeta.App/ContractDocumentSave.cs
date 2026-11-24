using System.Diagnostics;
using System.IO;
using System.Windows;
using ClosedXML.Excel;
using Microsoft.Win32;

namespace GmcSmeta.App;

/// <summary>
/// Export-only save path: dialog on the UI thread; render on a worker; write to
/// a sibling temporary file and publish only after a complete XLSX/PDF is ready.
/// Never modifies SQL data, and preserves the old destination on failure.
/// </summary>
public static class ContractDocumentSave
{
    public static async Task<bool> XlsxAsync(Window? owner, string proposedName, Func<XLWorkbook> create)
    {
        var path = Destination(owner, "Экспорт Excel", "Excel (*.xlsx)|*.xlsx", ".xlsx", proposedName);
        if (path is null) return false;
        return await SaveAsync(owner, path, temporary =>
        {
            using var workbook = create();
            if (workbook.Worksheets.Count == 0)
                throw new InvalidDataException("Книга не содержит листов.");
            workbook.SaveAs(temporary);
            using var check = new XLWorkbook(temporary);
            if (check.Worksheets.Count == 0)
                throw new InvalidDataException("Не удалось проверить сохранённую книгу.");
        }, ".xlsx");
    }

    public static async Task<bool> PdfAsync(Window? owner, string proposedName, Func<byte[]> create)
    {
        var path = Destination(owner, "Экспорт PDF", "PDF (*.pdf)|*.pdf", ".pdf", proposedName);
        if (path is null) return false;
        return await SaveAsync(owner, path, temporary =>
        {
            var bytes = create();
            if (bytes.Length < 100 || bytes[0] != '%' || bytes[1] != 'P' ||
                bytes[2] != 'D' || bytes[3] != 'F')
                throw new InvalidDataException("PDF не сформирован.");
            File.WriteAllBytes(temporary, bytes);
        }, ".pdf");
    }

    private static string? Destination(Window? owner, string title, string filter,
        string extension, string suggested)
    {
        var safe = string.Concat(suggested.Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
        var dialog = new SaveFileDialog
        {
            Title = title, Filter = filter, DefaultExt = extension,
            FileName = safe, AddExtension = true, OverwritePrompt = true
        };
        return (owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) == true
            ? dialog.FileName : null;
    }

    private static async Task<bool> SaveAsync(Window? owner, string destination,
        Action<string> generate, string extension)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        var temporary = Path.Combine(folder, ".gmc-export-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            await Task.Run(() =>
            {
                generate(temporary);
                File.Move(temporary, destination, true);
            });
        }
        catch (Exception error)
        {
            MessageBox.Show(owner, error.Message, "Экспорт документа",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { /* Temporary file cleanup must not mask the original error. */ }
        }

        try { Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true }); }
        catch { /* The saved file is valid without a local viewer. */ }
        return true;
    }
}
