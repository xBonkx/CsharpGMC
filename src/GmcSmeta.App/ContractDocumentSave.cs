using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using ClosedXML.Excel;
using GmcSmeta.Core;
using Microsoft.Win32;

namespace GmcSmeta.App;

/// <summary>
/// Export-only save path: dialog on the UI thread; render on a worker; write to
/// a sibling temporary file and publish only after a complete XLSX/PDF is ready.
/// Never modifies SQL data, and preserves the old destination on failure.
/// </summary>
public static class ContractDocumentSave
{
    public static Task<bool> XlsxAsync(Window? owner, string proposedName, Func<XLWorkbook> create) =>
        XlsxAsync(owner, proposedName, create, CancellationToken.None);

    public static async Task<bool> XlsxAsync(Window? owner, string proposedName,
        Func<XLWorkbook> create, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        var path = Destination(owner, "Экспорт Excel", "Excel (*.xlsx)|*.xlsx", ".xlsx", proposedName);
        if (path is null || cancellationToken.IsCancellationRequested) return false;
        return await SaveAsync(owner, path, stream =>
        {
            using var workbook = create();
            if (workbook.Worksheets.Count == 0)
                throw new InvalidDataException("Книга не содержит листов.");
            workbook.SaveAs(stream);
            stream.Flush();
            stream.Position = 0;
            using var check = new XLWorkbook(stream);
            if (check.Worksheets.Count == 0)
                throw new InvalidDataException("Не удалось проверить сохранённую книгу.");
        }, cancellationToken);
    }

    public static async Task<bool> PdfAsync(Window? owner, string proposedName, Func<byte[]> create)
    {
        var path = Destination(owner, "Экспорт PDF", "PDF (*.pdf)|*.pdf", ".pdf", proposedName);
        if (path is null) return false;
        return await SaveAsync(owner, path, stream =>
        {
            var bytes = create();
            if (bytes.Length < 100 || bytes[0] != '%' || bytes[1] != 'P' ||
                bytes[2] != 'D' || bytes[3] != 'F')
                throw new InvalidDataException("PDF не сформирован.");
            stream.Write(bytes);
        });
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
        Action<Stream> generate, CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var prepared = ExportFilePublication.Stage(destination, generate);
                // Before publication the old destination is still untouched.
                cancellationToken.ThrowIfCancellationRequested();
                prepared.Publish();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error)
        {
            if (cancellationToken.IsCancellationRequested) return false;
            MessageBox.Show(owner, error.Message, "Экспорт документа",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (cancellationToken.IsCancellationRequested) return true;
        try { Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true }); }
        catch { /* The saved file is valid without a local viewer. */ }
        return true;
    }
}
