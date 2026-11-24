using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Standalone XLSX report of persisted «изм.» revisions, no database writes.</summary>
public static class SpecUpdateExcelReport
{
    public static void Export(string path, IReadOnlyList<SpecSqlHistory> history)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Выбери путь для файла .xlsx.");
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Изменения");
        var headers = new[]
        {
            "Изм.", "Дата", "Пользователь", "Файл", "Вид изменения",
            "Система", "Старая система", "Наименование", "Ед. изм.",
            "Количество до", "Количество после", "Цена, ₽", "Примечание"
        };
        for (var col = 0; col < headers.Length; col++)
            sheet.Cell(1, col + 1).Value = headers[col];

        var currentRow = 2;
        foreach (var revision in history.OrderBy(r => r.Id))
        {
            using JsonDocument? json = Parse(revision.Data);
            var changes = json?.RootElement.ValueKind == JsonValueKind.Array
                ? json.RootElement.EnumerateArray().ToArray()
                : Array.Empty<JsonElement>();
            if (changes.Length == 0)
                WriteChange(sheet, currentRow++, revision, null);
            else
                foreach (var change in changes)
                    WriteChange(sheet, currentRow++, revision, change);
        }
        if (currentRow == 2)
        {
            sheet.Cell(2, 1).Value = "Обновлений пока нет.";
            currentRow++;
        }
        var headerRange = sheet.Range(1, 1, 1, headers.Length);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#E7EEF5");
        headerRange.Style.Alignment.WrapText = true;
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, currentRow - 1, headers.Length).SetAutoFilter();
        foreach (var col in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 })
            sheet.Column(col).Width = col switch
            {
                4 => 42, 6 or 7 => 26, 8 => 55, 13 => 58,
                2 => 20, 10 or 11 or 12 => 16, _ => 17
            };
        sheet.Columns(10, 12).Style.NumberFormat.Format = "#,##0.00";
        sheet.Column(8).Style.Alignment.WrapText = true;
        sheet.Column(13).Style.Alignment.WrapText = true;
        var summary = workbook.Worksheets.Add("Ревизии");
        var titles = new[] { "Изм.", "Дата", "Пользователь", "Файл", "Итого до, ₽", "Итого после, ₽", "Разница, ₽" };
        for (var i = 0; i < titles.Length; i++) summary.Cell(1, i + 1).Value = titles[i];
        for (var i = 0; i < history.Count; i++)
        {
            var item = history.OrderBy(x => x.Id).ElementAt(i);
            var row = i + 2;
            summary.Cell(row, 1).Value = item.Title;
            summary.Cell(row, 2).Value = item.CreatedAt;
            summary.Cell(row, 3).Value = item.User;
            summary.Cell(row, 4).Value = item.File;
            summary.Cell(row, 5).Value = (double)item.TotalBefore;
            summary.Cell(row, 6).Value = (double)item.TotalAfter;
            summary.Cell(row, 7).Value = (double)(item.TotalAfter - item.TotalBefore);
        }
        summary.Range(1, 1, 1, titles.Length).Style.Font.Bold = true;
        summary.Range(1, 1, 1, titles.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#E7EEF5");
        summary.Column(1).Width = 17;
        summary.Column(2).Width = 22;
        summary.Column(3).Width = 25;
        summary.Column(4).Width = 56;
        summary.Columns(5, 7).Width = 22;
        summary.Columns(5, 7).Style.NumberFormat.Format = "#,##0.00";
        summary.SheetView.FreezeRows(1);
        workbook.SaveAs(path);
    }

    private static JsonDocument? Parse(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        try { return JsonDocument.Parse(data); }
        catch (JsonException) { return null; }
    }

    private static void WriteChange(IXLWorksheet sheet, int row, SpecSqlHistory revision,
        JsonElement? change)
    {
        string Get(string key)
        {
            if (change is not JsonElement item || item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
                return "";
            return value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : value.ToString();
        }
        double? Number(string key) =>
            double.TryParse(Get(key), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        sheet.Cell(row, 1).Value = revision.Title;
        sheet.Cell(row, 2).Value = revision.CreatedAt;
        sheet.Cell(row, 3).Value = revision.User;
        sheet.Cell(row, 4).Value = revision.File;
        sheet.Cell(row, 5).Value = Get("kind");
        sheet.Cell(row, 6).Value = Get("system");
        sheet.Cell(row, 7).Value = Get("old_system");
        sheet.Cell(row, 8).Value = Get("name");
        sheet.Cell(row, 9).Value = Get("unit");
        if (Number("old_qty") is double before) sheet.Cell(row, 10).Value = before;
        if (Number("new_qty") is double after) sheet.Cell(row, 11).Value = after;
        if (Number("price") is double price) sheet.Cell(row, 12).Value = price;
        sheet.Cell(row, 13).Value = Get("warn");
    }
}
