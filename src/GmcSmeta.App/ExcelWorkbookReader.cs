using System.IO;
using ClosedXML.Excel;
using ExcelDataReader;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Reads cached spreadsheet values; never evaluates arbitrary Excel formulas.</summary>
internal static class ExcelWorkbookReader
{
    public static ExcelImportResult Read(string path, CancellationToken ct)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".xls" => ReadLegacy(path, ct),
            ".xlsx" or ".xlsm" => ReadOpenXml(path, ct),
            _ => throw new NotSupportedException("Ожидается Excel .xlsx, .xlsm или .xls.")
        };
    }

    private static ExcelImportResult ReadOpenXml(string path, CancellationToken ct)
    {
        using var workbook = new XLWorkbook(path);
        var emptyFormulaCache = 0;
        IEnumerable<(string Sheet, IReadOnlyList<IReadOnlyList<object?>> Rows)> Sheets()
        {
            foreach (var sheet in workbook.Worksheets)
            {
                ct.ThrowIfCancellationRequested();
                var used = sheet.RangeUsed(XLCellsUsedOptions.Contents);
                if (used is null) { yield return (sheet.Name, Array.Empty<IReadOnlyList<object?>>()); continue; }
                var lastRow = used.RangeAddress.LastAddress.RowNumber;
                var lastCol = used.RangeAddress.LastAddress.ColumnNumber;
                if (lastRow > 200_000 || lastCol > 300)
                    throw new InvalidDataException($"Лист «{sheet.Name}» слишком большой: {lastRow} строк, {lastCol} колонок.");
                var rows = new List<IReadOnlyList<object?>>(lastRow);
                for (var r = 1; r <= lastRow; r++)
                {
                    if ((r & 63) == 0) ct.ThrowIfCancellationRequested();
                    var values = new object?[lastCol];
                    for (var c = 1; c <= lastCol; c++)
                    {
                        var cell = sheet.Cell(r, c);
                        // CachedValue reads the value written by Excel; calling Value on a
                        // formula can trigger ClosedXML recalculation and change parity.
                        var value = cell.HasFormula ? cell.CachedValue : cell.Value;
                        if (cell.HasFormula && value.IsBlank) emptyFormulaCache++;
                        if (value.IsBlank) continue;
                        values[c - 1] = value.IsNumber ? value.GetNumber() :
                            value.IsDateTime ? value.GetDateTime() :
                            value.IsBoolean ? value.GetBoolean() : value.ToString();
                    }
                    rows.Add(values);
                }
                yield return (sheet.Name, rows);
            }
        }
        var result = ExcelImportParser.ParseWorkbook(path, Sheets(), ct);
        if (emptyFormulaCache > 0)
            result.Notes.Add($"Формулы без сохранённого результата: {emptyFormulaCache}. " +
                "Открой файл в Excel, пересчитай и сохрани его перед повторным импортом.");
        return result;
    }

    private static ExcelImportResult ReadLegacy(string path, CancellationToken ct)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateBinaryReader(stream);
        IEnumerable<(string Sheet, IReadOnlyList<IReadOnlyList<object?>> Rows)> Sheets()
        {
            do
            {
                var rows = new List<IReadOnlyList<object?>>();
                var count = 0;
                while (reader.Read())
                {
                    if ((++count & 63) == 0) ct.ThrowIfCancellationRequested();
                    if (count > 200_000 || reader.FieldCount > 300)
                        throw new InvalidDataException("Лист .xls превышает ограничение размера.");
                    var values = new object?[reader.FieldCount];
                    for (var c = 0; c < values.Length; c++) values[c] = reader.GetValue(c);
                    rows.Add(values);
                }
                yield return (reader.Name, rows);
            } while (reader.NextResult());
        }
        return ExcelImportParser.ParseWorkbook(path, Sheets(), ct);
    }
}
