using GmcSmeta.Core.Db;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Domain;

public sealed record ObjectProperties(string Name, string Customer = "", string Address = "",
    string Folder = "", string Note = "", string Form = "")
{
    public ObjectProperties Normalize()
    {
        var result = this with { Name = Name.Trim(), Customer = Customer.Trim(), Address = Address.Trim(), Folder = Folder.Trim() };
        if (result.Name.Length == 0) throw new ArgumentException("Укажите название.");
        return result;
    }
}
public sealed record ObjectTag(string Key, string Caption);

public sealed record CompanyInfo(long Id, string Name, decimal VatRate);

public sealed record ContractInfo(long Id, long ObjectId, string Kind, string Number, string Date,
    string Name, long? CompanyId, long? BaseContractId, double VatRate, string Note, string Form,
    bool Locked, long? StageOf, string SmrMode, double SmrPercent, double SmrAmount,
    double TransportAmount, string M2Mode, double PnrAmount, double? TransportCost,
    string TransportNote, string KsSitePart)
{
    public string Title => Documents.ContractTitleText(Kind, Number, Date, Name);
}

/// <summary>Поля диалога «Свойства документа» — как ContractDialog в Python.</summary>
public sealed record ContractProperties(string Kind = "kp", string Number = "", string Date = "",
    string Name = "", long? CompanyId = null, long? BaseContractId = null, decimal? VatRate = null,
    string Note = "", string Form = "")
{
    public ContractProperties Normalize()
    {
        var kind = Kind is "main" or "dop" ? Kind : "kp";
        return this with
        {
            Kind = kind,
            Number = Number.Trim(),
            Date = Date.Trim(),
            Name = Name.Trim(),
            Note = Note ?? "",
            BaseContractId = kind == "dop" ? BaseContractId : null,
        };
    }
}


/// <summary>
/// Перенос операций builder.py / services.py. Свойства объектов подключены к WPF;
/// операции редактирования сметы пока не подключены к интерфейсу.
/// Создание разделов и позиций сохраняет порядок групп; правки сметы проверяют locked.
/// Полный паритет UpdatePosition (каталог, формулы, цены актов) ещё не реализован.
/// </summary>
public static partial class Documents
{
    // ---------------------------------------------------------------- объекты и документы

    public static long CreateObject(SqliteConnection conn, string name, string customer = "",
                                    string address = "", string folder = "", string note = "", string form = "")
    {
        var values = new ObjectProperties(name, customer, address, folder, note, form).Normalize();
        using var tx = conn.BeginTransaction();
        var id = RowWriter.Insert(conn, "objects", new Dictionary<string, object?>
        {
            ["name"] = values.Name,
            ["customer"] = values.Customer,
            ["address"] = values.Address,
            ["folder"] = values.Folder,
            ["note"] = values.Note,
            ["form"] = values.Form,
        });
        ChangeLog.Log(conn, "object", id, "create", values.Name);
        tx.Commit();
        return id;
    }

    public static ObjectProperties ReadObject(SqliteConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name,customer,address,folder,note,form FROM objects WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new ArgumentException("Объект не найден.");
        string Text(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
        return new(Text(0), Text(1), Text(2), Text(3), Text(4), Text(5));
    }

    public static IReadOnlyList<ObjectTag> ObjectTags(SqliteConnection conn, string current = "")
    {
        using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT key,name FROM tags";
        using var r = cmd.ExecuteReader();
        var tags = new List<ObjectTag>();
        while (r.Read()) if (!r.IsDBNull(0) && r.GetString(0).Length > 0)
            tags.Add(new(r.GetString(0), r.IsDBNull(1) ? r.GetString(0) : r.GetString(1)));
        tags = tags.OrderBy(t => t.Caption, StringComparer.OrdinalIgnoreCase).ToList();
        tags.Insert(0, new("", "— обычный"));
        if (current.Length > 0 && !tags.Any(t => t.Key == current)) tags.Add(new(current, current));
        return tags;
    }

    public static bool UpdateObject(SqliteConnection conn, long id, ObjectProperties properties)
    {
        var value = properties.Normalize();
        using var tx = conn.BeginTransaction();
        var old = ReadObject(conn, id);
        var changes = new Dictionary<string, object?>(); var details = new List<string>();
        foreach (var (field, before, after) in new[] {
            ("name", old.Name, value.Name), ("customer", old.Customer, value.Customer),
            ("address", old.Address, value.Address), ("folder", old.Folder, value.Folder),
            ("note", old.Note, value.Note), ("form", old.Form, value.Form) })
        {
            if (before == after) continue;
            changes[field] = after; details.Add($"{field}: «{before}» → «{after}»");
        }
        if (changes.Count > 0)
        {
            RowWriter.Update(conn, "objects", id, changes);
            ChangeLog.Log(conn, "object", id, "update", string.Join("; ", details));
        }
        tx.Commit();
        return changes.Count > 0;
    }

    /// <summary>Переименовать объект (заголовок страницы, F2 в списке). Пустое или совпадающее имя ничего не пишет.</summary>
    public static bool RenameObject(SqliteConnection conn, long id, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return false;
        var old = ReadObject(conn, id);
        return old.Name != name && UpdateObject(conn, id, old with { Name = name });
    }

    /// <summary>Завершить объект (в архив) или вернуть в работу — как Python finish_object. Одна транзакция с историей.</summary>
    public static void SetObjectArchived(SqliteConnection conn, long id, bool archived)
    {
        using var tx = conn.BeginTransaction();
        var name = ScalarText(conn, "SELECT name FROM objects WHERE id = $id", id)
            ?? throw new ArgumentException("Объект не найден.");
        RowWriter.Update(conn, "objects", id, new Dictionary<string, object?>
        {
            ["archived"] = archived,
            ["archived_on"] = archived ? DateOnly.FromDateTime(DateTime.Today) : null,
        });
        ChangeLog.Log(conn, "object", id, archived ? "archive" : "unarchive", name);
        tx.Commit();
    }

    /// <summary>Изменить год в архиве (Python change_archive_year). 29 февраля переносится на 28-е.</summary>
    public static void ChangeArchiveYear(SqliteConnection conn, long id, int year)
    {
        using var tx = conn.BeginTransaction();
        string name; bool archived; string? archivedOn;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name, archived, archived_on FROM objects WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Объект не найден.");
            name = r.IsDBNull(0) ? "" : r.GetString(0);
            archived = !r.IsDBNull(1) && r.GetInt64(1) != 0;
            archivedOn = r.IsDBNull(2) ? null : r.GetString(2);
        }
        if (!archived) throw new InvalidOperationException("Объект не в архиве.");
        var current = DateOnly.TryParseExact(archivedOn, "yyyy-MM-dd", out var existing)
            ? existing.Year : DateTime.Today.Year;
        var date = existing != default ? existing : DateOnly.FromDateTime(DateTime.Today);
        DateOnly target;
        try { target = new DateOnly(year, date.Month, date.Day); }
        catch (ArgumentOutOfRangeException) { target = new DateOnly(year, 2, 28); }   // 29 февраля
        RowWriter.Update(conn, "objects", id, new Dictionary<string, object?> { ["archived_on"] = target });
        ChangeLog.Log(conn, "object", id, "archive_year", $"{name}: {current} → {year}");
        tx.Commit();
    }

    /// <summary>Удалить объект: полная копия в корзину, история и каскадное удаление — одна транзакция (services.delete_object).</summary>
    public static void DeleteObject(SqliteConnection conn, long id)
    {
        using var tx = conn.BeginTransaction();
        var name = ScalarText(conn, "SELECT name FROM objects WHERE id = $id", id)
            ?? throw new ArgumentException("Объект не найден.");
        Trash.KeepObject(conn, id);
        // Запись до каскадного DELETE: после него владелец уже не определяется.
        ChangeLog.Log(conn, "object", id, "delete", name);
        RowWriter.Delete(conn, "objects", id);
        tx.Commit();
    }

    public sealed record ObjectDeleteInfo(string Name, IReadOnlyList<string> Details);

    /// <summary>Удалить документ: полная копия в корзину, история и каскад — одна транзакция (services.delete_contract).</summary>
    public static void DeleteContract(SqliteConnection conn, long id)
    {
        using var tx = conn.BeginTransaction();
        var info = DescribeContractForDelete(conn, id);
        Trash.KeepContract(conn, id);
        // Запись до каскадного DELETE: после него владелец уже не определяется.
        ChangeLog.Log(conn, "contract", id, "delete", $"{info.ObjectName}: {info.Title}");
        RowWriter.Delete(conn, "contracts", id);
        tx.Commit();
    }

    public sealed record ContractDeleteInfo(string Title, string ObjectName, int Acts, int Movements,
        IReadOnlyList<string> Details);

    /// <summary>Что удаляется вместе с документом — текст подтверждений (kp_editor.delete_contract).</summary>
    public static ContractDeleteInfo DescribeContractForDelete(SqliteConnection conn, long id)
    {
        string title, objectName;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT c.kind, c.number, c.date, c.name, o.name FROM contracts c " +
                "JOIN objects o ON o.id = c.object_id WHERE c.id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Документ не найден.");
            string Text(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
            title = ContractTitleText(Text(0), Text(1), Text(2), Text(3));
            objectName = Text(4);
        }
        int acts, movements;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM acts WHERE contract_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            acts = Convert.ToInt32(cmd.ExecuteScalar());
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT COUNT(*) FROM movements m JOIN positions p ON p.id = m.position_id " +
                "JOIN systems s ON s.id = p.system_id WHERE s.contract_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            movements = Convert.ToInt32(cmd.ExecuteScalar());
        }
        var details = new List<string>();
        if (acts > 0) details.Add($"акты КС-2/КС-3: {acts}");
        if (movements > 0) details.Add($"история закупок и выдачи: {movements} записей");
        return new ContractDeleteInfo(title, objectName, acts, movements, details);
    }

    /// <summary>Что удаляется вместе с объектом — текст первого подтверждения (main_window.delete_object).</summary>
    public static ObjectDeleteInfo DescribeObjectForDelete(SqliteConnection conn, long id)
    {
        var name = ScalarText(conn, "SELECT name FROM objects WHERE id = $id", id)
            ?? throw new ArgumentException("Объект не найден.");
        var details = new List<string>();
        var titles = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT kind, number, date, name FROM contracts WHERE object_id = $id ORDER BY \"order\", id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var kind = r.IsDBNull(0) ? "kp" : r.GetString(0);
                titles.Add(ContractTitleText(kind, r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3)));
            }
        }
        if (titles.Count > 0)
            details.Add("документы: " + titles.Count + " (" + string.Join(", ", titles.Take(4))
                + (titles.Count > 4 ? " …" : "") + ")");
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT COUNT(*), COALESCE(SUM(CASE WHEN signed THEN 1 ELSE 0 END), 0) FROM acts " +
                "WHERE contract_id IN (SELECT id FROM contracts WHERE object_id = $id)";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (r.Read() && r.GetInt64(0) > 0)
                details.Add("акты КС-2/КС-3: " + r.GetInt64(0)
                    + (r.GetInt64(1) > 0 ? $", из них подписанных: {r.GetInt64(1)}" : ""));
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM shipments WHERE object_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            var count = Convert.ToInt64(cmd.ExecuteScalar());
            if (count > 0) details.Add("ведомости выдачи: " + count);
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT COUNT(*) FROM movements WHERE position_id IN (" +
                "SELECT id FROM positions WHERE system_id IN (" +
                "SELECT id FROM systems WHERE contract_id IN (SELECT id FROM contracts WHERE object_id = $id)))";
            cmd.Parameters.AddWithValue("$id", id);
            var count = Convert.ToInt64(cmd.ExecuteScalar());
            if (count > 0) details.Add($"история закупок и выдачи: {count} записей");
        }
        return new(name, details);
    }

    public static long CreateContract(SqliteConnection conn, long objectId, string kind = "kp",
                                      string number = "", string date = "", string name = "",
                                      long? companyId = null, long? baseContractId = null,
                                      decimal vatRate = 22m)
    {
        using var tx = conn.BeginTransaction();
        var form = ScalarText(conn, "SELECT form FROM objects WHERE id = $id", objectId) ?? "";
        var id = RowWriter.Insert(conn, "contracts", new Dictionary<string, object?>
        {
            ["object_id"] = objectId,
            ["kind"] = kind,
            ["name"] = name,
            ["number"] = number,
            ["date"] = date,
            ["kp_phase"] = kind == "kp" ? "volumes" : "",
            ["company_id"] = companyId,
            ["base_contract_id"] = baseContractId,
            ["vat_rate"] = vatRate,
            ["order"] = NextContractOrder(conn, objectId),
            ["form"] = form,          // новый документ получает тег объекта (правила заказчика)
        });
        ChangeLog.Log(conn, "contract", id, "create", $"{ObjectName(conn, objectId)}: {ContractTitle(conn, id)}");
        tx.Commit();
        return id;
    }

    private static long NextContractOrder(SqliteConnection conn, long objectId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(\"order\"), 0) + 1 FROM contracts WHERE object_id = $id";
        cmd.Parameters.AddWithValue("$id", objectId);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 1L);
    }

    private static string ObjectName(SqliteConnection conn, long id) =>
        ScalarText(conn, "SELECT name FROM objects WHERE id = $id", id) ?? "";

    private static string ContractTitle(SqliteConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT c.kind, c.number, c.date, c.name FROM contracts c WHERE c.id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return "";
        return ContractTitleText(r.IsDBNull(0) ? "kp" : r.GetString(0),
            r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
            r.IsDBNull(3) ? "" : r.GetString(3));
    }

    /// <summary>Как Contract.title в Python: «Договор № 12 от 01.02.2026 — название».</summary>
    // ---------------------------------------------------------------- свойства и копирование документов

    /// <summary>Наши организации в порядке Python builder.companies (order, id).</summary>
    public static IReadOnlyList<CompanyInfo> Companies(SqliteConnection conn)
    {
        var list = new List<CompanyInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, vat_rate FROM companies ORDER BY \"order\", id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new CompanyInfo(r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? 0m : (decimal)r.GetDouble(2)));
        return list;
    }

    public static decimal? CompanyVatRate(SqliteConnection conn, long? companyId)
    {
        if (companyId is null) return null;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT vat_rate FROM companies WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", companyId.Value);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : (decimal)Convert.ToDouble(value);
    }

    /// <summary>Организация последнего документа объекта — как last_company в new_contract Python.</summary>
    public static long? LastCompanyId(SqliteConnection conn, long objectId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT company_id FROM contracts WHERE object_id = $id AND company_id IS NOT NULL " +
            "ORDER BY \"order\" DESC, id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$id", objectId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    public static ContractInfo ReadContract(SqliteConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT object_id, kind, number, date, name, company_id, base_contract_id, " +
            "vat_rate, note, form, locked, stage_of, smr_mode, smr_percent, smr_amount, " +
            "transport_amount, m2_mode, pnr_amount, transport_cost, transport_note, ks_site_part " +
            "FROM contracts WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new ArgumentException("Документ не найден.");
        string Text(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
        long? Id(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        double Dbl(int i) => r.IsDBNull(i) ? 0d : r.GetDouble(i);
        double? DblN(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
        return new ContractInfo(id, r.GetInt64(0), Text(1), Text(2), Text(3), Text(4),
            Id(5), Id(6), Dbl(7), Text(8), Text(9),
            !r.IsDBNull(10) && r.GetInt64(10) != 0, Id(11), Text(12), Dbl(13), Dbl(14),
            Dbl(15), Text(16), Dbl(17), DblN(18), Text(19), Text(20));
    }

    /// <summary>Дата документа в Python — строка «дд.мм.гггг»; пустая тоже допустима.</summary>
    /// <summary>
    /// Изменить реквизиты документа (services.update_contract + dialog values): тип, номер, дата,
    /// название, организация, договор для ДС, НДС, тег и примечание. История — в той же транзакции.
    /// </summary>
    public static bool UpdateContract(SqliteConnection conn, long id, ContractProperties properties)
    {
        var value = properties.Normalize();
        using var tx = conn.BeginTransaction();
        RequireContractPropertiesEditable(conn, id);
        var old = ReadContract(conn, id);

        var changes = new Dictionary<string, object?>();
        var details = new List<string>();
        void Compare(string field, object? before, object? after)
        {
            var same = before is null ? after is null : before.Equals(after);
            if (same) return;
            changes[field] = after;
            details.Add($"{field}: <{before ?? ""}> → <{after ?? ""}>");
        }

        Compare("kind", old.Kind, value.Kind);
        Compare("number", old.Number, value.Number);
        Compare("date", old.Date, value.Date);
        Compare("name", old.Name, value.Name);
        Compare("company_id", old.CompanyId, value.CompanyId);
        Compare("base_contract_id", old.BaseContractId, value.BaseContractId);

        // НДС: явно заданный — как есть; при смене организации — по организации, как в диалоге Python.
        var vat = (double)(value.VatRate ?? (decimal)old.VatRate);
        if (value.VatRate is null && value.CompanyId != old.CompanyId &&
            CompanyVatRate(conn, value.CompanyId) is decimal companyVat)
            vat = (double)companyVat;
        Compare("vat_rate", old.VatRate, vat);

        Compare("note", old.Note, value.Note);
        Compare("form", old.Form, value.Form);

        if (changes.Count > 0)
        {
            RowWriter.Update(conn, "contracts", id, changes);
            ChangeLog.Log(conn, "contract", id, "update", string.Join("; ", details));
        }
        tx.Commit();
        return changes.Count > 0;
    }

    /// <summary>
    /// Копия документа (builder.copy_contract): реквизиты, режимы СМР/транспорта/тег и,
    /// если <paramref name="withPositions"/>, все разделы с позициями и комплектующими.
    /// Строки «вне КП» не копируются — в смете они стали бы строками с нулевой ценой.
    /// </summary>
    public static long CopyContract(SqliteConnection conn, long sourceId, string kind = "kp",
                                    string number = "", string date = "", string name = "",
                                    bool withPositions = true, long? baseContractId = null,
                                    long? companyId = null, decimal? vatRate = null)
    {
        using var tx = conn.BeginTransaction();
        var src = ReadContract(conn, sourceId);
        var company = companyId ?? src.CompanyId;
        var companyVat = companyId is not null
            ? CompanyVatRate(conn, company) is decimal rate ? (double)rate : src.VatRate
            : src.VatRate;
        var vat = vatRate is decimal givenVat ? (double)givenVat : companyVat;

        var id = RowWriter.Insert(conn, "contracts", new Dictionary<string, object?>
        {
            ["object_id"] = src.ObjectId,
            ["kind"] = kind,
            ["number"] = number,
            ["date"] = date,
            ["name"] = name,
            ["kp_phase"] = kind == "kp" ? "volumes" : "",
            ["company_id"] = company,
            ["base_contract_id"] = baseContractId,
            ["vat_rate"] = vat,
            ["order"] = NextContractOrder(conn, src.ObjectId),
            ["form"] = src.Form,
            ["smr_mode"] = src.SmrMode,
            ["smr_percent"] = src.SmrPercent,
            ["smr_amount"] = src.SmrAmount,
            ["transport_amount"] = src.TransportAmount,
            ["m2_mode"] = src.M2Mode,
            ["pnr_amount"] = src.PnrAmount,
            ["transport_cost"] = src.TransportCost,
            ["transport_note"] = src.TransportNote,
            ["ks_site_part"] = src.KsSitePart,
        });

        if (withPositions)
        {
            var made = new Dictionary<long, long>();
            foreach (var sy in LoadSystemCopies(conn, sourceId))
            {
                var parent = sy.ParentId is long parentId && made.TryGetValue(parentId, out var mapped)
                    ? mapped : (long?)null;
                var newSystem = RowWriter.Insert(conn, "systems", new Dictionary<string, object?>
                {
                    ["contract_id"] = id,
                    ["parent_id"] = parent,
                    ["name"] = sy.Name,
                    ["order"] = sy.Order,
                    ["smr_amount"] = sy.SmrAmount,
                    ["heading"] = sy.Heading,
                    ["source_num"] = sy.SourceNum,
                });
                made[sy.Id] = newSystem;
                CopyPositions(conn, sy.Id, newSystem);
            }
        }

        // Как Python: create_contract пишет «create», затем copy_contract — «copy».
        ChangeLog.Log(conn, "contract", id, "create",
            $"{ObjectName(conn, src.ObjectId)}: {ContractTitle(conn, id)}");
        ChangeLog.Log(conn, "contract", id, "copy", $"из «{src.Title}»");
        tx.Commit();
        return id;
    }

    private sealed record SystemCopy(long Id, long? ParentId, long Order, string Name,
                                     string Heading, double SmrAmount, string SourceNum);

    private static List<SystemCopy> LoadSystemCopies(SqliteConnection conn, long contractId)
    {
        var list = new List<SystemCopy>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, parent_id, \"order\", name, heading, smr_amount, source_num " +
            "FROM systems WHERE contract_id = $id ORDER BY \"order\", id";
        cmd.Parameters.AddWithValue("$id", contractId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SystemCopy(
                r.GetInt64(0),
                r.IsDBNull(1) ? null : r.GetInt64(1),
                r.IsDBNull(2) ? 0 : r.GetInt64(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? "" : r.GetString(4),
                r.IsDBNull(5) ? 0d : r.GetDouble(5),
                r.IsDBNull(6) ? "" : r.GetString(6)));
        return list;
    }

    private sealed record PositionCopy(long Id, long? ParentId, long? CatalogItemId,
        string Num, string Basis, string Name, string Unit, double Qty, double Price,
        double? PurchasePrice, string Note, string ExtraJson, long? SourceRow, long Order,
        bool OutOfKp, string Formulas, double? AreaManual, double? WorkPrice,
        string CostNote, string CostColor, double? WorkSell, double? WorkCost);

    private static List<PositionCopy> LoadPositionCopies(SqliteConnection conn, long systemId)
    {
        var list = new List<PositionCopy>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, parent_id, catalog_item_id, num, basis, name, unit, qty, price, " +
            "purchase_price, note, extra_json, source_row, \"order\", out_of_kp, formulas, " +
            "area_manual, work_price, cost_note, cost_color, work_sell, work_cost " +
            "FROM positions WHERE system_id = $id ORDER BY \"order\", id";
        cmd.Parameters.AddWithValue("$id", systemId);
        using var r = cmd.ExecuteReader();
        string Text(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
        long? Id(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        double Dbl(int i) => r.IsDBNull(i) ? 0d : r.GetDouble(i);
        double? DblN(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
        while (r.Read())
            list.Add(new PositionCopy(
                r.GetInt64(0), Id(1), Id(2), Text(3), Text(4), Text(5), Text(6),
                Dbl(7), Dbl(8), DblN(9), Text(10), Text(11), Id(12),
                r.IsDBNull(13) ? 0 : r.GetInt64(13), Dbl(14) != 0, Text(15),
                DblN(16), DblN(17), Text(18), Text(19), DblN(20), DblN(21)));
        return list;
    }

    /// <summary>
    /// Скопировать позиции раздела в новый раздел: порядок показа (верхняя строка, сразу её
    /// комплектующие), ссылки parent → новые id, «вне КП» и их комплектующие пропускаются.
    /// </summary>
    private static void CopyPositions(SqliteConnection conn, long sourceSystemId, long targetSystemId)
    {
        var rows = LoadPositionCopies(conn, sourceSystemId);
        var byId = rows.ToDictionary(p => p.Id);

        var children = new Dictionary<long, List<PositionCopy>>();
        foreach (var p in rows.Where(p => p.ParentId is not null))
        {
            if (!children.TryGetValue(p.ParentId!.Value, out var list))
                children[p.ParentId.Value] = list = new List<PositionCopy>();
            list.Add(p);
        }

        var ordered = new List<PositionCopy>();
        foreach (var top in rows.Where(p => p.ParentId is null).OrderBy(p => p.Order))
        {
            ordered.Add(top);
            if (children.TryGetValue(top.Id, out var kids)) ordered.AddRange(kids.OrderBy(p => p.Order));
        }

        var made = new Dictionary<long, long>();
        foreach (var p in ordered)
        {
            if (p.OutOfKp) continue;
            if (p.ParentId is long parentId && byId.TryGetValue(parentId, out var parent) && parent.OutOfKp)
                continue;

            var newId = RowWriter.Insert(conn, "positions", new Dictionary<string, object?>
            {
                ["system_id"] = targetSystemId,
                ["parent_id"] = p.ParentId is long pid && made.TryGetValue(pid, out var mapped)
                    ? mapped : (long?)null,
                ["catalog_item_id"] = p.CatalogItemId,
                ["num"] = p.Num,
                ["basis"] = p.Basis,
                ["name"] = p.Name,
                ["unit"] = p.Unit,
                ["qty"] = p.Qty,
                ["price"] = p.Price,
                ["purchase_price"] = p.PurchasePrice,
                ["note"] = p.Note,
                ["extra_json"] = p.ExtraJson,
                ["source_row"] = p.SourceRow,
                ["order"] = p.Order,
                ["formulas"] = p.Formulas,
                ["area_manual"] = p.AreaManual,
                ["work_price"] = p.WorkPrice,
                ["cost_note"] = p.CostNote,
                ["cost_color"] = p.CostColor,
                ["work_sell"] = p.WorkSell,
                ["work_cost"] = p.WorkCost,
            });
            made[p.Id] = newId;
        }
    }

    private static void RequireContractPropertiesEditable(SqliteConnection conn, long contractId)
    {
        var info = ReadContract(conn, contractId);
        if (info.Locked)
            throw new InvalidOperationException("Документ подписан. Изменение реквизитов недоступно.");
        if (info.StageOf is not null)
            throw new InvalidOperationException("Это этап другого документа. Реквизиты меняются у полного КП.");
    }

    // ---------------------------------------------------------------- системы

    /// <summary>Добавить систему (или подраздел при <paramref name="parentSystemId"/>) в конец.</summary>
    public static long AddSystem(SqliteConnection conn, long contractId, string name, long? parentSystemId = null)
    {
        using var tx = conn.BeginTransaction();

        RequireEditable(conn, contractId);
        var systems = LoadSystems(conn, contractId);
        if (parentSystemId is not null)
        {
            var parent = systems.FirstOrDefault(s => s.Id == parentSystemId)
                ?? throw new ArgumentException("Родительский раздел не принадлежит документу.");
            // Как builder.add_subsection: подраздел подраздела относится к корневой системе.
            parentSystemId = parent.ParentId ?? parent.Id;
        }
        var id = RowWriter.Insert(conn, "systems", new Dictionary<string, object?>
        {
            ["contract_id"] = contractId,
            ["name"] = name.Trim(),
            ["parent_id"] = parentSystemId,
            ["order"] = systems.Count,
        });

        systems.Add(new SystemRow(id, parentSystemId, systems.Count));
        RenumberSystems(conn, GroupSystems(systems));

        var parentName = parentSystemId is null
            ? name.Trim()
            : $"{systems.First(s => s.Id == parentSystemId).Name} > {name.Trim()}";
        ChangeLog.Log(conn, "system", id, "create", $"{ContractTitle(conn, contractId)}: {parentName}");
        tx.Commit();
        return id;
    }

    private sealed record SystemRow(long Id, long? ParentId, long Order, string Name = "");

    private static List<SystemRow> LoadSystems(SqliteConnection conn, long contractId)
    {
        var list = new List<SystemRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, parent_id, \"order\", name FROM systems WHERE contract_id = $id ORDER BY \"order\", id";
        cmd.Parameters.AddWithValue("$id", contractId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SystemRow(
                r.GetInt64(0),
                r.IsDBNull(1) ? null : r.GetInt64(1),
                r.IsDBNull(2) ? 0 : r.GetInt64(2),
                r.IsDBNull(3) ? "" : r.GetString(3)));
        return list;
    }

    /// <summary>
    /// Разделы как группы: [система, её подразделы…]. Подраздел всегда идёт сразу за своей системой —
    /// так же, как _groups в Python.
    /// </summary>
    private static List<List<SystemRow>> GroupSystems(List<SystemRow> systems)
    {
        var ids = systems.Select(s => s.Id).ToHashSet();
        var byParent = new Dictionary<long, List<SystemRow>>();
        foreach (var s in systems.Where(s => s.ParentId is not null && ids.Contains(s.ParentId.Value)))
        {
            if (!byParent.TryGetValue(s.ParentId!.Value, out var list))
                byParent[s.ParentId.Value] = list = new List<SystemRow>();
            list.Add(s);
        }

        var groups = new List<List<SystemRow>>();
        foreach (var s in systems.Where(s => s.ParentId is null || !ids.Contains(s.ParentId.Value)))
        {
            var group = new List<SystemRow> { s };
            if (byParent.TryGetValue(s.Id, out var subs)) group.AddRange(subs);
            groups.Add(group);
        }
        return groups;
    }

    private static void RenumberSystems(SqliteConnection conn, List<List<SystemRow>> groups)
    {
        var order = 0;
        foreach (var s in groups.SelectMany(g => g))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE systems SET \"order\" = $o WHERE id = $id";
            cmd.Parameters.AddWithValue("$o", order++);
            cmd.Parameters.AddWithValue("$id", s.Id);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- позиции

    /// <summary>
    /// Добавить позицию. <paramref name="parentPositionId"/> — подпозиция (комплектующая),
    /// <paramref name="afterId"/> — вставить сразу после указанной позиции (со всеми её подпозициями).
    /// </summary>
    public static long AddPosition(SqliteConnection conn, long systemId, string name, string unit = "шт",
                                   decimal qty = 1m, decimal price = 0m,
                                   long? parentPositionId = null, long? afterId = null,
                                   decimal? purchasePrice = null)
    {
        using var tx = conn.BeginTransaction();

        RequireEditable(conn, SystemContract(conn, systemId));
        var positions = LoadPositions(conn, systemId);
        var ordered = OrderPositions(positions);

        var insertAt = ordered.Count;
        if (parentPositionId is not null)
        {
            var parent = positions.FirstOrDefault(p => p.Id == parentPositionId)
                ?? throw new ArgumentException("Родительская позиция не принадлежит разделу.");
            if (parent.ParentId is not null)
                throw new ArgumentException("Комплектующая не может содержать свои комплектующие.");
            var kids = ordered.Where(p => p.ParentId == parentPositionId).ToList();
            insertAt = kids.Count > 0
                ? ordered.IndexOf(kids[^1]) + 1
                : ordered.FindIndex(p => p.Id == parentPositionId) + 1;
        }
        else if (afterId is not null)
        {
            var anchor = positions.FirstOrDefault(p => p.Id == afterId)
                ?? throw new ArgumentException("Позиция вставки не принадлежит разделу.");
            insertAt = SubtreeEnd(ordered, anchor.ParentId ?? anchor.Id);
        }

        var id = RowWriter.Insert(conn, "positions", new Dictionary<string, object?>
        {
            ["system_id"] = systemId,
            ["parent_id"] = parentPositionId,
            ["catalog_item_id"] = Catalog.ItemId(conn, name, unit),   // как builder.add_position
            ["name"] = name,
            ["unit"] = unit,
            ["qty"] = qty,
            ["price"] = parentPositionId is null ? price : 0m,   // у комплектующей своей цены нет
            ["purchase_price"] = purchasePrice,
            ["order"] = ordered.Count,
        });

        ordered.Insert(Math.Clamp(insertAt, 0, ordered.Count),
            new PositionRow(id, parentPositionId, ordered.Count));
        RenumberPositions(conn, ordered);

        tx.Commit();
        return id;
    }

    private sealed record PositionRow(long Id, long? ParentId, long Order, string Name = "");

    private static List<PositionRow> LoadPositions(SqliteConnection conn, long systemId)
    {
        var list = new List<PositionRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, parent_id, \"order\", name FROM positions WHERE system_id = $id " +
            "ORDER BY \"order\", id";
        cmd.Parameters.AddWithValue("$id", systemId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new PositionRow(
                r.GetInt64(0),
                r.IsDBNull(1) ? null : r.GetInt64(1),
                r.IsDBNull(2) ? 0 : r.GetInt64(2),
                r.IsDBNull(3) ? "" : r.GetString(3)));
        return list;
    }

    /// <summary>Порядок показа: верхняя позиция, сразу под ней её подпозиции (builder.ordered_positions).</summary>
    private static List<PositionRow> OrderPositions(List<PositionRow> positions)
    {
        var children = new Dictionary<long, List<PositionRow>>();
        foreach (var p in positions.Where(p => p.ParentId is not null))
        {
            if (!children.TryGetValue(p.ParentId!.Value, out var list))
                children[p.ParentId.Value] = list = new List<PositionRow>();
            list.Add(p);
        }

        var ordered = new List<PositionRow>();
        foreach (var top in positions.Where(p => p.ParentId is null).OrderBy(p => p.Order))
        {
            ordered.Add(top);
            if (children.TryGetValue(top.Id, out var kids))
                ordered.AddRange(kids.OrderBy(p => p.Order));
        }
        return ordered;
    }

    /// <summary>Индекс сразу после позиции и всех её подпозиций.</summary>
    private static int SubtreeEnd(List<PositionRow> ordered, long id)
    {
        var start = ordered.FindIndex(p => p.Id == id);
        if (start < 0) return ordered.Count;

        var end = start + 1;
        while (end < ordered.Count && ordered[end].ParentId == id) end++;
        return end;
    }

    private static void RenumberPositions(SqliteConnection conn, List<PositionRow> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE positions SET \"order\" = $o WHERE id = $id";
            cmd.Parameters.AddWithValue("$o", i);
            cmd.Parameters.AddWithValue("$id", ordered[i].Id);
            cmd.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- изменения

    /// <summary>Изменить поля позиции и записать это в историю.</summary>
    public static void UpdatePosition(SqliteConnection conn, long id,
                                      decimal? qty = null, decimal? price = null,
                                      string? name = null, string? unit = null, string? note = null)
    {
        var values = new Dictionary<string, object?>();
        if (qty is not null) values["qty"] = qty.Value;
        if (price is not null) values["price"] = price.Value;
        if (name is not null) values["name"] = name;
        if (unit is not null) values["unit"] = unit;
        if (note is not null) values["note"] = note;
        if (values.Count == 0) return;

        using var tx = conn.BeginTransaction();
        RequireEditable(conn, PositionContract(conn, id));
        RowWriter.Update(conn, "positions", id, values);
        ChangeLog.Log(conn, "position", id, "update",
            string.Join(", ", values.Keys.Select(k => $"{k} = {values[k]}")));
        tx.Commit();
    }

    public static int DeletePosition(SqliteConnection conn, long id)
    {
        using var tx = conn.BeginTransaction();
        RequireEditable(conn, PositionContract(conn, id));
        var name = ScalarText(conn, "SELECT name FROM positions WHERE id = $id", id) ?? "";
        using (var guard = conn.CreateCommand())
        {
            guard.CommandText = "SELECT COUNT(*) FROM act_lines WHERE position_id IN " +
                "(SELECT id FROM positions WHERE id = $id OR parent_id = $id)";
            guard.Parameters.AddWithValue("$id", id);
            if (Convert.ToInt64(guard.ExecuteScalar()) > 0)
                throw new InvalidOperationException("Позиция или её комплектующая уже есть в КС-2. Сначала уберите её из акта.");
        }
        var systemId = long.Parse(ScalarText(conn, "SELECT system_id FROM positions WHERE id = $id", id)!);
        // Запись до DELETE: после каскадного удаления владелец уже не определяется.
        ChangeLog.Log(conn, "position", id, "delete", name);
        var deleted = RowWriter.Delete(conn, "positions", id);
        RenumberPositions(conn, OrderPositions(LoadPositions(conn, systemId)));
        tx.Commit();
        return deleted;
    }

    // ---------------------------------------------------------------- перемещение и комплектующие

    /// <summary>Позиция и её место: раздел, родитель, имя. Нужна для команд строк.</summary>
    private sealed record PositionInfo(long Id, long SystemId, long? ParentId, string Name);

    private static PositionInfo ReadPositionInfo(SqliteConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, system_id, parent_id, name FROM positions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new ArgumentException("Позиция не найдена.");
        return new PositionInfo(
            r.GetInt64(0),
            r.GetInt64(1),
            r.IsDBNull(2) ? null : r.GetInt64(2),
            r.IsDBNull(3) ? "" : r.GetString(3));
    }

    /// <summary>
    /// Передвинуть позицию среди соседей — перенос builder.move_position.
    /// Верхняя строка идёт вместе с комплектующими, комплектующая — только внутри своего родителя.
    /// </summary>
    public static bool MovePosition(SqliteConnection conn, long id, int delta)
    {
        using var tx = conn.BeginTransaction();
        var info = ReadPositionInfo(conn, id);
        RequireEditable(conn, SystemContract(conn, info.SystemId));
        var ordered = OrderPositions(LoadPositions(conn, info.SystemId));

        if (info.ParentId is long parentId)
        {
            var siblings = ordered.Where(p => p.ParentId == parentId)
                                  .OrderBy(p => p.Order).ThenBy(p => p.Id).ToList();
            var i = siblings.FindIndex(p => p.Id == id);
            if (i < 0) return false;
            var j = Math.Clamp(i + delta, 0, siblings.Count - 1);
            if (i == j) return false;

            var orders = siblings.Select(p => p.Order).ToList();
            var moving = siblings[i];
            siblings.RemoveAt(i);
            siblings.Insert(j, moving);
            for (var k = 0; k < siblings.Count; k++)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE positions SET \"order\" = $o WHERE id = $id";
                cmd.Parameters.AddWithValue("$o", orders[k]);
                cmd.Parameters.AddWithValue("$id", siblings[k].Id);
                cmd.ExecuteNonQuery();
            }
        }
        else
        {
            var tops = ordered.Where(p => p.ParentId is null)
                              .OrderBy(p => p.Order).ThenBy(p => p.Id).ToList();
            var i = tops.FindIndex(p => p.Id == id);
            if (i < 0) return false;
            var j = Math.Clamp(i + delta, 0, tops.Count - 1);
            if (i == j) return false;

            var moving = tops[i];
            tops.RemoveAt(i);
            tops.Insert(j, moving);

            var rebuilt = new List<PositionRow>();
            foreach (var top in tops)
            {
                rebuilt.Add(top);
                rebuilt.AddRange(ordered.Where(p => p.ParentId == top.Id)
                                        .OrderBy(p => p.Order).ThenBy(p => p.Id));
            }
            RenumberPositions(conn, rebuilt);
        }

        tx.Commit();
        return true;
    }

    /// <summary>
    /// Сделать строку комплектующей строки выше или снова обычной — перенос builder.make_subposition.
    /// False — команда неприменима (нет строки выше / у строки уже есть комплектующие).
    /// </summary>
    public static bool MakeSubposition(SqliteConnection conn, long id, bool makeSub)
    {
        using var tx = conn.BeginTransaction();
        var info = ReadPositionInfo(conn, id);
        RequireEditable(conn, SystemContract(conn, info.SystemId));
        var ordered = OrderPositions(LoadPositions(conn, info.SystemId));

        string? log = null;
        if (makeSub && info.ParentId is null)
        {
            var index = ordered.FindIndex(p => p.Id == id);
            var above = ordered.Take(index).Where(p => p.ParentId is null).ToList();
            if (above.Count == 0 || ordered.Any(p => p.ParentId == id)) return false;

            var parent = above[^1];
            RowWriter.Update(conn, "positions", id,
                new Dictionary<string, object?> { ["parent_id"] = parent.Id });
            log = $"{Truncate(info.Name, 60)}: стала комплектующей «{Truncate(parent.Name, 40)}»";
        }
        else if (!makeSub && info.ParentId is not null)
        {
            RowWriter.Update(conn, "positions", id,
                new Dictionary<string, object?> { ["parent_id"] = null });
            log = $"{Truncate(info.Name, 60)}: больше не комплектующая";
        }
        else
        {
            return false;
        }

        RenumberPositions(conn, OrderPositions(LoadPositions(conn, info.SystemId)));
        ChangeLog.Log(conn, "position", id, "update", log);
        tx.Commit();
        return true;
    }

    /// <summary>
    /// Перенести верхние строки (с комплектующими) в конец другого раздела —
    /// перенос builder.move_positions_to_system. Комплектующие по одной не переносятся.
    /// </summary>
    public static int MovePositionsToSystem(SqliteConnection conn, IEnumerable<long> ids, long targetSystemId)
    {
        using var tx = conn.BeginTransaction();
        var targetContract = SystemContract(conn, targetSystemId);
        RequireEditable(conn, targetContract);

        var moved = 0;
        var touched = new HashSet<long> { targetSystemId };
        foreach (var id in ids.Distinct())
        {
            var info = ReadPositionInfo(conn, id);
            if (info.ParentId is not null || info.SystemId == targetSystemId) continue;
            if (SystemContract(conn, info.SystemId) != targetContract)
                throw new ArgumentException("Строки другого документа нельзя переносить сюда.");

            touched.Add(info.SystemId);
            var baseOrder = CountPositions(conn, targetSystemId);
            var children = OrderPositions(LoadPositions(conn, info.SystemId))
                .Where(p => p.ParentId == id).ToList();

            RowWriter.Update(conn, "positions", id,
                new Dictionary<string, object?>
                {
                    ["system_id"] = targetSystemId,
                    ["order"] = baseOrder,
                });
            for (var k = 0; k < children.Count; k++)
                RowWriter.Update(conn, "positions", children[k].Id,
                    new Dictionary<string, object?>
                    {
                        ["system_id"] = targetSystemId,
                        ["order"] = baseOrder + 1 + k,
                    });

            ChangeLog.Log(conn, "position", id, "move",
                $"{Truncate(info.Name, 60)} → система {SystemName(conn, targetSystemId)}");
            moved++;
        }

        foreach (var systemId in touched)
            RenumberPositions(conn, OrderPositions(LoadPositions(conn, systemId)));

        tx.Commit();
        return moved;
    }

    private static long CountPositions(SqliteConnection conn, long systemId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM positions WHERE system_id = $id";
        cmd.Parameters.AddWithValue("$id", systemId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string SystemName(SqliteConnection conn, long systemId) =>
        ScalarText(conn, "SELECT name FROM systems WHERE id = $id", systemId) ?? "";

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..length];

    // ---------------------------------------------------------------- разделы

    /// <summary>Переименовать раздел — builder.rename_system: пустое и прежнее имя не пишутся.</summary>
    public static bool RenameSystem(SqliteConnection conn, long id, string name)
    {
        using var tx = conn.BeginTransaction();
        var contractId = SystemContract(conn, id);
        RequireEditable(conn, contractId);

        var old = SystemName(conn, id);
        var next = (name ?? "").Trim();
        if (next.Length == 0 || string.Equals(old, next, StringComparison.Ordinal))
            return false;

        ChangeLog.Log(conn, "system", id, "rename", $"«{old}» → «{next}»");
        RowWriter.Update(conn, "systems", id, new Dictionary<string, object?> { ["name"] = next });
        tx.Commit();
        return true;
    }

    /// <summary>
    /// СМР раздела — ячейка «Примечание» заголовка при режиме СМР «по системам».
    /// Как kp_editor._save_cell: пишет только реально изменившуюся сумму.
    /// </summary>
    public static bool SetSystemSmr(SqliteConnection conn, long id, decimal? amount)
    {
        using var tx = conn.BeginTransaction();
        var contractId = SystemContract(conn, id);
        RequireEditable(conn, contractId);

        var label = SystemLabel(conn, id);
        var old = SystemSmr(conn, id) ?? 0m;
        var next = amount ?? 0m;
        if (old == next) return false;

        ChangeLog.Log(conn, "system", id, "update",
            $"{label}: СМР {Money.Format(old)} → {Money.Format(next)}");
        RowWriter.Update(conn, "systems", id,
            new Dictionary<string, object?> { ["smr_amount"] = amount });
        tx.Commit();
        return true;
    }

    private static decimal? SystemSmr(SqliteConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT smr_amount FROM systems WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var found = cmd.ExecuteScalar();
        return found is null or DBNull ? null : Money.FromDouble(Convert.ToDouble(found));
    }

    /// <summary>«Родитель › раздел» или просто имя — как System.label в Python.</summary>
    private static string SystemLabel(SqliteConnection conn, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT s.name, p.name FROM systems s LEFT JOIN systems p ON p.id = s.parent_id " +
            "WHERE s.id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new ArgumentException("Раздел не найден.");
        var name = r.IsDBNull(0) ? "" : r.GetString(0);
        return r.IsDBNull(1) ? name : $"{r.GetString(1)} › {name}";
    }

    /// <summary>Передвинуть раздел (с подразделами) или подраздел внутри своего раздела.</summary>
    public static bool MoveSystem(SqliteConnection conn, long id, int delta)
    {
        using var tx = conn.BeginTransaction();
        var contractId = SystemContract(conn, id);
        RequireEditable(conn, contractId);
        var systems = LoadSystems(conn, contractId);
        var current = systems.FirstOrDefault(s => s.Id == id);
        if (current is null) return false;

        var groups = GroupSystems(systems);
        if (current.ParentId is long parentId)
        {
            var group = groups.FirstOrDefault(g => g[0].Id == parentId);
            if (group is null) return false;
            var subs = group.Skip(1).ToList();
            var i = subs.FindIndex(s => s.Id == id);
            var j = Math.Clamp(i + delta, 0, subs.Count - 1);
            if (i < 0 || i == j) return false;
            var moving = subs[i];
            subs.RemoveAt(i);
            subs.Insert(j, moving);
            group.RemoveRange(1, group.Count - 1);
            group.AddRange(subs);
        }
        else
        {
            var i = groups.FindIndex(g => g[0].Id == id);
            if (i < 0) return false;
            var j = Math.Clamp(i + delta, 0, groups.Count - 1);
            if (i == j) return false;
            var moving = groups[i];
            groups.RemoveAt(i);
            groups.Insert(j, moving);
        }

        RenumberSystems(conn, groups);
        tx.Commit();
        return true;
    }

    /// <summary>
    /// Удалить раздел вместе с подразделами и строками — builder.delete_system.
    /// Строки, уже попавшие в КС-2, блокируют удаление. Возвращает число удалённых строк.
    /// </summary>
    public static int DeleteSystem(SqliteConnection conn, long id)
    {
        using var tx = conn.BeginTransaction();
        var contractId = SystemContract(conn, id);
        RequireEditable(conn, contractId);

        var systems = LoadSystems(conn, contractId);
        var current = systems.FirstOrDefault(s => s.Id == id);
        if (current is null) return 0;
        var doomed = new List<SystemRow> { current };
        doomed.AddRange(systems.Where(s => s.ParentId == id));

        var positionIds = new List<long>();
        var counts = new Dictionary<long, int>();
        foreach (var system in doomed)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM positions WHERE system_id = $id";
            cmd.Parameters.AddWithValue("$id", system.Id);
            using var r = cmd.ExecuteReader();
            var list = new List<long>();
            while (r.Read()) list.Add(r.GetInt64(0));
            counts[system.Id] = list.Count;
            positionIds.AddRange(list);
        }
        GuardActs(conn, positionIds);

        var title = ContractTitle(conn, contractId);
        var deleted = 0;
        for (var i = doomed.Count - 1; i >= 0; i--)
        {
            var system = doomed[i];
            ChangeLog.Log(conn, "system", system.Id, "delete",
                $"{title}: {SystemLabel(conn, system.Id)} ({counts[system.Id]} поз.)");
            // Явно, не надеясь только на ON DELETE CASCADE: удаление не должно оставлять сирот.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM positions WHERE system_id = $id";
                cmd.Parameters.AddWithValue("$id", system.Id);
                cmd.ExecuteNonQuery();
            }
            deleted += RowWriter.Delete(conn, "systems", system.Id);
        }
        RenumberSystems(conn, GroupSystems(LoadSystems(conn, contractId)));
        tx.Commit();
        return positionIds.Count;
    }

    private static void GuardActs(SqliteConnection conn, IReadOnlyList<long> positionIds)
    {
        if (positionIds.Count == 0) return;
        var placeholders = string.Join(", ", positionIds.Select((_, i) => "$p" + i));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM act_lines WHERE position_id IN (" + placeholders + ")";
        for (var i = 0; i < positionIds.Count; i++) cmd.Parameters.AddWithValue("$p" + i, positionIds[i]);
        if (Convert.ToInt64(cmd.ExecuteScalar()) > 0)
            throw new InvalidOperationException(
                "Строка уже есть в КС-2. Сначала уберите её из акта, потом удаляйте.");
    }

    private static long SystemContract(SqliteConnection conn, long systemId) =>
        long.Parse(ScalarText(conn, "SELECT contract_id FROM systems WHERE id = $id", systemId)
            ?? throw new ArgumentException("Раздел не найден."));

    private static long PositionContract(SqliteConnection conn, long positionId) =>
        long.Parse(ScalarText(conn,
            "SELECT sy.contract_id FROM positions p JOIN systems sy ON sy.id = p.system_id WHERE p.id = $id",
            positionId) ?? throw new ArgumentException("Позиция не найдена."));

    private static void RequireEditable(SqliteConnection conn, long contractId)
    {
        var locked = ScalarText(conn, "SELECT locked FROM contracts WHERE id = $id", contractId)
            ?? throw new ArgumentException("Документ не найден.");
        if (locked != "0")
            throw new InvalidOperationException("Смета подписана. Изменение недоступно.");
    }

    private static string? ScalarText(SqliteConnection conn, string sql, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value);
    }
}
