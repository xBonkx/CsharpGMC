using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GmcSmeta.Core.Db;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Domain;

/// <summary>Одно поле позиции, которое меняет редактор сметы.</summary>
public sealed record PositionChange(string Field, object? Value);

/// <summary>Результат правки ячейки: сохранено/изменено/сообщение об ошибке.</summary>
public sealed record CellSaveResult(bool Saved, bool Changed, string? Error)
{
    public static CellSaveResult Fail(string error) => new(false, false, error);
}

/// <summary>
/// Правка позиции сметы — перенос builder.update_position и сохранения ячейки kp_editor.
/// Меняет только реально изменившиеся поля, ведёт историю в формате Python, сбрасывает
/// устаревшие формулы, обновляет каталог, цены в незакрытых актах и площадь «м²».
/// </summary>
public static partial class PositionEditor
{
    /// <summary>Числовые поля, у которых бывает формула.</summary>
    private static readonly string[] NumericFields =
        { "qty", "price", "purchase_price", "work_price" };

    private static readonly JsonSerializerOptions FormulaJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Изменить поля позиции и записать историю. False — значений не изменилось.</summary>
    public static bool UpdatePosition(SqliteConnection conn, long id, params PositionChange[] changes)
    {
        using var tx = conn.BeginTransaction();
        var snapshot = Load(conn, id);
        if (snapshot.Locked)
            throw new InvalidOperationException("Смета подписана. Изменение недоступно.");
        var changed = Apply(conn, snapshot, changes);
        if (snapshot.ParentId is long parentId)
            Area.RefreshSummary(conn, parentId);
        tx.Commit();
        return changed;
    }

    /// <summary>
    /// Сохранить одну ячейку как редактор Python: разобрать число/формулу, проверить знак,
    /// записать значение и формулу одной транзакцией. Ошибка не меняет базу.
    /// </summary>
    /// <param name="fixMode">Режим «исправление подписанного»: разрешает текстовые ячейки.</param>
    public static CellSaveResult SaveCell(SqliteConnection conn, long id, string field, string text,
                                          bool fixMode = false)
    {
        using var tx = conn.BeginTransaction();
        var snapshot = Load(conn, id);

        if (snapshot.Locked)
        {
            var textColumn = field is "name" or "unit" or "note";
            if (field != "purchase_price" && !(fixMode && textColumn))
                return CellSaveResult.Fail("Смета подписана. Изменение недоступно.");
        }

        var changes = new List<PositionChange>();
        string? formula = null;

        switch (field)
        {
            case "qty":
            case "price":
            case "purchase_price":
            case "work_price":
            {
                var trimmed = (text ?? "").Trim();
                var number = trimmed.Length == 0 ? null : NumberText.Parse(trimmed);
                if (trimmed.Length > 0 && number is null)
                    return CellSaveResult.Fail(
                        $"Не число: <{trimmed}>. Введите число, например 12 или 1 250,50");
                if (number < 0m)
                    return CellSaveResult.Fail($"Отрицательное число ввели вручную: <{trimmed}>");

                object? value = number;
                if (number is null)
                    value = field is "purchase_price" or "work_price" ? null : 0m;
                changes.Add(new PositionChange(field, value));
                // «=4+5» / «12*1,15»: в ячейке число, в редакторе — снова формула.
                formula = NumberText.FormulaText(text);
                break;
            }
            case "name":
            {
                var name = (text ?? "").Trim();
                changes.Add(new PositionChange("name", name));
                // Как Python: имя из каталога подставляет единицу, но НЕ цену.
                var unit = name.Length > 0 && !string.Equals(name, snapshot.Name, StringComparison.Ordinal)
                    ? CatalogUnit(conn, name)
                    : null;
                if (!string.IsNullOrEmpty(unit))
                    changes.Add(new PositionChange("unit", unit));
                break;
            }
            case "unit":
                changes.Add(new PositionChange("unit", (text ?? "").Trim()));
                break;
            case "note":
                changes.Add(new PositionChange("note", (text ?? "").Trim()));
                break;
            case "num":
                changes.Add(new PositionChange("num", Truncate((text ?? "").Trim(), 32)));
                break;
            case "basis":
                changes.Add(new PositionChange("basis", Truncate((text ?? "").Trim(), 64)));
                break;
            case "area_manual":
            {
                var trimmed = (text ?? "").Trim();
                decimal? number = null;
                if (trimmed.Length > 0)
                {
                    number = NumberText.Parse(trimmed);
                    if (number is null)
                        return CellSaveResult.Fail(
                            $"Не число: <{trimmed}>. Введите число, например 12 или 1 250,50");
                    if (number < 0m)
                        return CellSaveResult.Fail($"Отрицательное число ввели вручную: <{trimmed}>");
                }

                // Python пишет area_manual напрямую, без history: это не значение сметы, а способ счёта м².
                if (Same(snapshot.AreaManual, number))
                    return new CellSaveResult(true, false, null);
                RowWriter.Update(conn, "positions", id,
                    new Dictionary<string, object?> { ["area_manual"] = number });
                if (snapshot.ParentId is long summaryId)
                    Area.RefreshSummary(conn, summaryId);
                tx.Commit();
                return new CellSaveResult(true, true, null);
            }
            default:
                return CellSaveResult.Fail($"Неизвестное поле позиции: {field}");
        }

        var changed = Apply(conn, snapshot, changes);

        // Python всегда переписывает формулу после числа: обычное число стирает старую формулу.
        if (formula is not null || field is "qty" or "price" or "purchase_price" or "work_price")
        {
            var current = LoadFormulas(conn, id);
            var updated = SetFormula(current, field, formula);
            if (!string.Equals(updated, current, StringComparison.Ordinal))
            {
                RowWriter.Update(conn, "positions", id,
                    new Dictionary<string, object?> { ["formulas"] = updated });
                changed = true;
            }
        }

        // Строка внутри строки «м²»: площадь сводной строки пересчитывается.
        if (snapshot.ParentId is long parentId)
            Area.RefreshSummary(conn, parentId);

        tx.Commit();
        return new CellSaveResult(true, changed, null);
    }

    /// <summary>Записать изменения; вызывается внутри транзакции. True — что-то изменилось.</summary>
    private static bool Apply(SqliteConnection conn, PositionSnapshot old,
        IReadOnlyList<PositionChange> changes)
    {
        var changed = new List<(string Field, object? Value, string OldText)>();
        foreach (var change in changes)
        {
            var before = old.Get(change.Field);
            if (Same(before, change.Value)) continue;
            changed.Add((change.Field, change.Value, PythonText(before)));
        }
        if (changed.Count == 0) return false;

        var values = new Dictionary<string, object?>();
        foreach (var (field, value, _) in changed) values[field] = value;

        // Число, введённое заново, делает старую формулу неверной — как builder.update_position.
        var formulas = old.Formulas;
        var formulaChanged = false;
        foreach (var field in NumericFields)
        {
            if (!changes.Any(c => c.Field == field)) continue;
            if (Formula(formulas, field).Length == 0) continue;
            formulas = SetFormula(formulas, field, null);
            formulaChanged = true;
        }

        // Имя/единица заново привязывают позицию к каталогу.
        if (changes.Any(c => c.Field is "name" or "unit"))
        {
            var name = TextValue(values, "name", old.Name);
            var unit = TextValue(values, "unit", old.Unit);
            values["catalog_item_id"] = name.Trim().Length == 0
                ? null
                : CatalogItemId(conn, name, unit);
        }
        if (formulaChanged) values["formulas"] = formulas;

        RowWriter.Update(conn, "positions", old.Id, values);

        if (changes.Any(c => c.Field == "price"))
        {
            // builder.update_position: цены незакрытых актов следуют за сметой.
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE act_lines SET price = $price WHERE position_id = $id AND act_id IN " +
                "(SELECT id FROM acts WHERE signed = 0)";
            cmd.Parameters.AddWithValue("$price", (double)DecimalValue(values, "price", old.Price));
            cmd.Parameters.AddWithValue("$id", old.Id);
            cmd.ExecuteNonQuery();
        }

        if (changes.Any(c => c.Field == "work_price"))
        {
            var work = NullableDecimalValue(values, "work_price", old.WorkPrice) ?? 0m;
            var actIds = new List<long>();
            using (var cmd = conn.CreateCommand())
            {
                // Python: line.work_price = p.work_price or 0.0
                cmd.CommandText =
                    "UPDATE act_lines SET work_price = $work WHERE position_id = $id AND act_id IN " +
                    "(SELECT id FROM acts WHERE signed = 0) RETURNING act_id";
                cmd.Parameters.AddWithValue("$work", (double)work);
                cmd.Parameters.AddWithValue("$id", old.Id);
                using var r = cmd.ExecuteReader();
                while (r.Read()) actIds.Add(r.GetInt64(0));
            }
            if (old.SmrMode == "pos")
                foreach (var actId in actIds.Distinct()) Area.SyncActSmr(conn, actId);
        }

        var newName = TextValue(values, "name", old.Name);
        var details = $"{old.ContractTitle} / {old.SystemName} / {Truncate(newName, 60)}: " +
                      string.Join("; ", changed.Select(c =>
                          $"{c.Field}: «{c.OldText}» → «{PythonText(c.Value)}»"));
        ChangeLog.Log(conn, "position", old.Id, "update", details);
        return true;
    }

    // ---------------------------------------------------------------- каталог и формулы

    /// <summary>services.catalog_key: регистр, пробелы и точка в единице не важны.</summary>
    public static string CatalogKey(string name, string unit) => Catalog.Key(name, unit);

    /// <summary>Единица из каталога по имени (как self.catalog в Python): пусто, если имени нет.</summary>
    private static string? CatalogUnit(SqliteConnection conn, string name) => Catalog.Unit(conn, name);

    /// <summary>Найти или создать элемент каталога по нормализованному имени и единице.</summary>
    private static long CatalogItemId(SqliteConnection conn, string name, string unit) =>
        Catalog.ItemId(conn, name, unit) ?? 0;

    private static string Formula(string formulas, string field)
    {
        if (string.IsNullOrWhiteSpace(formulas)) return "";
        try
        {
            var node = JsonNode.Parse(formulas) as JsonObject;
            return node?[field]?.GetValue<string>() ?? "";
        }
        catch (JsonException) { return ""; }
    }

    /// <summary>
    /// Установить/убрать формулу. Пишет так же, как Python json.dumps: «{"qty": "=4+5"}»,
    /// с пробелом после двоеточия и без экранирования русских букв.
    /// </summary>
    private static string SetFormula(string formulas, string field, string? text)
    {
        JsonObject node;
        try { node = JsonNode.Parse(string.IsNullOrWhiteSpace(formulas) ? "{}" : formulas) as JsonObject
                        ?? new JsonObject(); }
        catch (JsonException) { node = new JsonObject(); }

        if (string.IsNullOrEmpty(text)) node.Remove(field);
        else node[field] = text;
        if (node.Count == 0) return "";

        var parts = new List<string>();
        foreach (var (key, value) in node)
            parts.Add($"{JsonSerializer.Serialize(key, FormulaJson)}: " +
                      $"{JsonSerializer.Serialize(value?.GetValue<string>() ?? "", FormulaJson)}");
        return "{" + string.Join(", ", parts) + "}";
    }

    private static string LoadFormulas(SqliteConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT formulas FROM positions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var found = cmd.ExecuteScalar();
        return found is null or DBNull ? "" : (string)found;
    }

    // ---------------------------------------------------------------- снимок строки

    private sealed class PositionSnapshot
    {
        public long Id;
        public long? ParentId;
        public string Name = "";
        public string Unit = "";
        public string Note = "";
        public string Num = "";
        public string Basis = "";
        public decimal Qty;
        public decimal Price;
        public decimal? PurchasePrice;
        public decimal? WorkPrice;
        public decimal? AreaManual;
        public bool OutOfKp;
        public string Formulas = "";
        public long? CatalogItemId;
        public string SystemName = "";
        public string ContractTitle = "";
        public string SmrMode = "";
        public bool Locked;

        public object? Get(string field) => field switch
        {
            "name" => Name,
            "unit" => Unit,
            "note" => Note,
            "num" => Num,
            "basis" => Basis,
            "qty" => Qty,
            "price" => Price,
            "purchase_price" => PurchasePrice,
            "work_price" => WorkPrice,
            "area_manual" => AreaManual,
            "out_of_kp" => OutOfKp,
            "catalog_item_id" => CatalogItemId,
            _ => throw new ArgumentException($"неизвестное поле позиции: {field}"),
        };
    }

    private static PositionSnapshot Load(SqliteConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT p.name, p.unit, p.note, p.num, p.basis, p.qty, p.price, p.purchase_price, " +
            "p.work_price, p.area_manual, p.out_of_kp, p.formulas, p.catalog_item_id, " +
            "s.name, c.kind, c.number, c.date, c.name, c.smr_mode, c.locked, p.parent_id " +
            "FROM positions p JOIN systems s ON s.id = p.system_id " +
            "JOIN contracts c ON c.id = s.contract_id WHERE p.id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new ArgumentException("Позиция не найдена.");
        return new PositionSnapshot
        {
            Id = id,
            Name = Text(r, 0),
            Unit = Text(r, 1),
            Note = Text(r, 2),
            Num = Text(r, 3),
            Basis = Text(r, 4),
            Qty = Number(r, 5),
            Price = Number(r, 6),
            PurchasePrice = NumberOrNull(r, 7),
            WorkPrice = NumberOrNull(r, 8),
            AreaManual = NumberOrNull(r, 9),
            OutOfKp = !r.IsDBNull(10) && r.GetInt64(10) != 0,
            Formulas = Text(r, 11),
            CatalogItemId = r.IsDBNull(12) ? null : r.GetInt64(12),
            SystemName = Text(r, 13),
            ContractTitle = Documents.ContractTitleText(Text(r, 14), Text(r, 15), Text(r, 16), Text(r, 17)),
            SmrMode = Text(r, 18),
            Locked = !r.IsDBNull(19) && r.GetInt64(19) != 0,
            ParentId = r.IsDBNull(20) ? null : r.GetInt64(20),
        };
    }

    private static string Text(SqliteDataReader r, int index) => r.IsDBNull(index) ? "" : r.GetString(index);
    private static decimal Number(SqliteDataReader r, int index) =>
        r.IsDBNull(index) ? 0m : Money.FromDouble(r.GetDouble(index));
    private static decimal? NumberOrNull(SqliteDataReader r, int index) =>
        r.IsDBNull(index) ? null : Money.FromDouble(r.GetDouble(index));

    // ---------------------------------------------------------------- значения и сравнение

    private static string TextValue(IReadOnlyDictionary<string, object?> values, string field, string fallback) =>
        values.TryGetValue(field, out var v) ? (string?)v ?? "" : fallback;

    private static decimal DecimalValue(IReadOnlyDictionary<string, object?> values, string field,
                                        decimal fallback) =>
        values.TryGetValue(field, out var v) && v is decimal d ? d : fallback;

    private static decimal? NullableDecimalValue(IReadOnlyDictionary<string, object?> values, string field,
                                                 decimal? fallback) =>
        values.TryGetValue(field, out var v) ? v as decimal? : fallback;

    /// <summary>Сравнение как в Python: строки посимвольно, числа после перевода в double.</summary>
    private static bool Same(object? old, object? value)
    {
        if (old is null || value is null) return old is null && value is null;
        if (old is string os) return value is string vs && string.Equals(os, vs, StringComparison.Ordinal);
        if (old is bool ob) return value is bool vb && ob == vb;
        if (old is decimal od) return ToDouble(value) is { } vd && (double)od == vd;
        return Equals(old, value);
    }

    private static double? ToDouble(object? value) => value switch
    {
        null => null,
        decimal d => (double)d,
        double d => d,
        int i => i,
        long l => l,
        _ => null,
    };

    /// <summary>Как str() в Python: None, True/False, 2.0, 12.34.</summary>
    private static string PythonText(object? value) => value switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        decimal d => FloatText((double)d),
        double d => FloatText(d),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static string FloatText(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (!text.Contains('.') && !text.Contains('e') && !text.Contains('E')) text += ".0";
        return text;
    }

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..length];
}
