using System.Globalization;
using System.Text;

namespace GmcSmeta.Core.Db;

/// <summary>Validated columns pasted from Excel: Name, Unit, Qty, Sell, Purchase, Note.</summary>
public sealed record PastedPosition(string Name, string Unit, decimal Qty, decimal Price,
    decimal? PurchasePrice, string Note);

/// <summary>
/// Parses clipboard TSV including Excel's quoted cells with embedded tabs/newlines.
/// Parse everything before opening a database connection: invalid input must insert nothing.
/// </summary>
public static class PastedPositions
{
    public const int MaxRows = 5000;
    public const int MaxTextLength = 2_000_000;

    public static IReadOnlyList<PastedPosition> Parse(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new FormatException("Буфер обмена пуст.");
        if (source.Length > MaxTextLength)
            throw new FormatException("Слишком большой фрагмент Excel (не более 2 млн символов).");
        var result = new List<PastedPosition>();
        var rowIndex = 0;
        foreach (var columns in ParseTsv(source))
        {
            rowIndex++;
            if (columns.All(string.IsNullOrWhiteSpace)) continue;
            if (rowIndex == 1 && columns.Count >= 2 &&
                columns[0].Trim().Equals("Наименование", StringComparison.OrdinalIgnoreCase)) continue;
            if (columns.Count > 6)
                throw new FormatException($"Строка {rowIndex}: максимум 6 колонок (название, ед., количество, цена, закупка, примечание).");
            var name = columns[0].Trim();
            if (name.Length is 0 or > 2000)
                throw new FormatException($"Строка {rowIndex}: название должно содержать 1–2000 символов.");
            var unit = columns.Count > 1 && !string.IsNullOrWhiteSpace(columns[1]) ? columns[1].Trim() : "шт";
            if (unit.Length > 32)
                throw new FormatException($"Строка {rowIndex}: единица измерения длиннее 32 символов.");
            var qty = ReadNumber(columns, 2, 1m, rowIndex, "количество");
            var price = ReadNumber(columns, 3, 0m, rowIndex, "цена");
            decimal? purchase = columns.Count > 4 && !string.IsNullOrWhiteSpace(columns[4])
                ? ReadNumber(columns, 4, 0m, rowIndex, "закупка") : null;
            var note = columns.Count > 5 ? columns[5].Trim() : "";
            if (note.Length > 10_000)
                throw new FormatException($"Строка {rowIndex}: примечание длиннее 10 000 символов.");
            result.Add(new PastedPosition(name, unit, qty, price, purchase, note));
            if (result.Count > MaxRows)
                throw new FormatException($"Максимум {MaxRows} позиций за одну вставку.");
        }
        if (result.Count == 0) throw new FormatException("Не найдено позиций для вставки.");
        return result;
    }

    private static decimal ReadNumber(IReadOnlyList<string> fields, int index, decimal fallback,
        int row, string label)
    {
        if (fields.Count <= index || string.IsNullOrWhiteSpace(fields[index])) return fallback;
        var raw = fields[index].Trim().Replace(" ", "").Replace("\u00a0", "")
            .Replace("\u202f", "").Replace(',', '.');
        if (!decimal.TryParse(raw, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var value) || value < 0 || !double.IsFinite((double)value))
            throw new FormatException($"Строка {row}: некорректное значение «{label}» — {fields[index]}.");
        return value;
    }

    private static IEnumerable<List<string>> ParseTsv(string data)
    {
        var fields = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < data.Length; i++)
        {
            var c = data[i];
            if (c == '"')
            {
                if (quoted && i + 1 < data.Length && data[i + 1] == '"')
                {
                    cell.Append('"'); i++;
                }
                else if (quoted || cell.Length == 0) quoted = !quoted;
                else cell.Append(c);
            }
            else if (!quoted && (c == '\t' || c == '\r' || c == '\n'))
            {
                fields.Add(cell.ToString()); cell.Clear();
                if (c != '\t')
                {
                    yield return fields;
                    fields = new List<string>();
                    if (c == '\r' && i + 1 < data.Length && data[i + 1] == '\n') i++;
                }
            }
            else cell.Append(c);
        }
        if (quoted) throw new FormatException("Незакрытые кавычки в данных Excel.");
        if (cell.Length > 0 || fields.Count > 0)
        {
            fields.Add(cell.ToString());
            yield return fields;
        }
    }
}
