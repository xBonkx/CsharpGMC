using System.Data;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Python kp_editor._save_group: the whole group is edited as ONE SQL Server
/// serializable transaction. Unlike a loop over SaveCell, failure halfway
/// through never leaves a partly updated estimate or half-bumped revisions.
/// </summary>
public static partial class SqlServerDatabase
{
    public sealed record GroupSaveResult(bool Saved, int Changed, string? Error, string Revision)
    {
        public static GroupSaveResult Fail(string message, string revision) =>
            new(false, 0, message, revision);
    }

    private sealed record GroupPosition(long Id, string Name, string Unit,
        string SystemName, decimal Price, decimal? Purchase, decimal? Work,
        string? Formulas, bool Top, bool Outside);

    public static GroupSaveResult SaveGroup(string connectionString, long contractId,
        string groupKey, IReadOnlyList<long> positionIds, string field, string input,
        string expectedRevision)
    {
        // A grouped row is created from two or more *top-level* contract
        // positions; never allow arbitrary caller-provided IDs or SQL fields.
        if (contractId <= 0 || positionIds is null || positionIds.Count < 2 ||
            positionIds.Count > 10000 || string.IsNullOrWhiteSpace(groupKey))
            return GroupSaveResult.Fail("Недопустимая группа позиций.", expectedRevision);
        if (field is not ("name" or "unit" or "price" or "purchase_price" or "work_price"))
            return GroupSaveResult.Fail("Эту колонку нельзя менять группой.", expectedRevision);
        if (positionIds.Count != positionIds.Distinct().Count() ||
            positionIds.Any(id => id <= 0))
            return GroupSaveResult.Fail("В группе есть повторяющиеся или некорректные ID.", expectedRevision);

        var numeric = field is "price" or "purchase_price" or "work_price";
        var trimmed = (input ?? "").Trim();
        decimal? number = null;
        if (numeric)
        {
            number = trimmed.Length == 0 ? null : NumberText.Parse(trimmed);
            if (trimmed.Length > 0 && number is null)
                return GroupSaveResult.Fail("Некорректное число или формула.", expectedRevision);
            if (number < 0)
                return GroupSaveResult.Fail("Отрицательные цены недопустимы.", expectedRevision);
            if (field != "purchase_price" && number is null) number = 0m;
        }
        if (field == "name" && (trimmed.Length == 0 || trimmed.Length > 255))
            return GroupSaveResult.Fail("Название должно содержать от 1 до 255 символов.", expectedRevision);
        if (field == "unit" && trimmed.Length > 32)
            return GroupSaveResult.Fail("Единица измерения длиннее 32 символов.", expectedRevision);

        var targetDb = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (!targetDb.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            return GroupSaveResult.Fail("Групповые изменения разрешены только в GmcSmetaPilot_*.", expectedRevision);

        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = LockRevision(conn, tx);
        if (revision is null)
            return GroupSaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(revision, expectedRevision, StringComparison.Ordinal))
            return GroupSaveResult.Fail("Документ изменён в другом окне. Обнови его.", revision);

        long objectId;
        string docTitle, smrMode;
        bool locked;
        using (var cmd = new SqlCommand(
            "SELECT object_id, name, locked, smr_mode " +
            "FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return GroupSaveResult.Fail("Документ не найден.", revision);
            objectId = SqlInteger.ReadInt64(r, 0);
            docTitle = r.IsDBNull(1) ? "" : r.GetString(1);
            locked = !r.IsDBNull(2) && SqlFlag.Read(r, 2);
            smrMode = r.IsDBNull(3) ? "" : r.GetString(3);
        }
        if (locked && field != "purchase_price")
            return GroupSaveResult.Fail("Подписанный документ: менять клиентские данные нельзя.", revision);
        if (field == "work_price" && smrMode != "pos")
            return GroupSaveResult.Fail("Работы можно редактировать по позициям только при СМР «по позициям».", revision);

        // Fetch all top-level positions, not merely the IDs passed by the UI.
        // The group must still contain EXACTLY these IDs, even if a filter
        // was active when the command started.
        var rows = new List<GroupPosition>();
        using (var cmd = new SqlCommand(
            "SELECT p.id, p.name, p.unit, sy.name, p.price, p.purchase_price, " +
            "p.work_price, p.formulas, p.parent_id, p.out_of_kp " +
            "FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@cid ORDER BY p.id", conn, tx))
        {
            cmd.Parameters.Add("@cid", SqlDbType.BigInt).Value = contractId;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new GroupPosition(
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? 0m : Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                    reader.IsDBNull(5) ? null : Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture),
                    reader.IsDBNull(6) ? null : Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8),
                    !reader.IsDBNull(9) && Convert.ToBoolean(reader.GetValue(9), CultureInfo.InvariantCulture)));
        }

        if (!EstimateGroups.SameMembers(rows.Select(r =>
                (r.Id, r.Name, r.Unit, r.Top, r.Outside)), groupKey, positionIds))
            return GroupSaveResult.Fail(
                "Состав группы изменился или выбранные позиции не принадлежат одной группе.", revision);

        var selected = positionIds.ToHashSet();
        var changeRows = rows.Where(r => selected.Contains(r.Id)).ToArray();
        var changed = 0;
        var touchedActs = new HashSet<long>();
        var catalogCache = new Dictionary<string, long>(StringComparer.Ordinal);

        long? CatalogId(string name, string unit)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var key = Catalog.Key(name, unit);
            if (key.Length > 600)
                throw new InvalidOperationException("Слишком длинное название для справочника.");
            if (catalogCache.TryGetValue(key, out var cached)) return cached;
            using (var find = new SqlCommand(
                "SELECT id FROM dbo.catalog_items WITH (UPDLOCK,HOLDLOCK) WHERE [key]=@key", conn, tx))
            {
                find.Parameters.Add("@key", SqlDbType.NVarChar, 600).Value = key;
                var found = find.ExecuteScalar();
                if (found is not null and not DBNull)
                {
                    var id = Convert.ToInt64(found, CultureInfo.InvariantCulture);
                    return catalogCache[key] = id;
                }
            }
            using var add = new SqlCommand(
                "INSERT INTO dbo.catalog_items ([key],name,unit) OUTPUT INSERTED.id " +
                "VALUES (@key,@name,@unit)", conn, tx);
            add.Parameters.Add("@key", SqlDbType.NVarChar, 600).Value = key;
            add.Parameters.Add("@name", SqlDbType.NVarChar, -1).Value = name;
            add.Parameters.Add("@unit", SqlDbType.NVarChar, 32).Value = unit;
            var created = Convert.ToInt64(add.ExecuteScalar(), CultureInfo.InvariantCulture);
            return catalogCache[key] = created;
        }

        foreach (var row in changeRows)
        {
            var previousText = field == "name" ? row.Name : row.Unit;
            decimal? previousNumber = field switch
            {
                "price" => row.Price, "purchase_price" => row.Purchase,
                "work_price" => row.Work, _ => null
            };
            string? formulas = null;
            if (numeric)
            {
                JsonObject values;
                try
                {
                    values = string.IsNullOrWhiteSpace(row.Formulas) ? new JsonObject() :
                        JsonNode.Parse(row.Formulas) as JsonObject ??
                            throw new FormatException("Некорректный JSON формул.");
                }
                catch (Exception ex) when (ex is JsonException or FormatException)
                {
                    throw new InvalidOperationException(
                        $"Нельзя безопасно изменить формулы позиции {row.Id}: {ex.Message}", ex);
                }
                var formula = NumberText.FormulaText(input);
                if (formula is null) values.Remove(field);
                else values[field] = formula;
                formulas = values.ToJsonString(new JsonSerializerOptions
                { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                // Unchanged numbers with different formula are still a write.
                if (previousNumber == number && JsonEquivalent(row.Formulas, formulas))
                    continue;
            }
            else if (string.Equals(previousText, trimmed, StringComparison.Ordinal))
                continue;

            var catalogId = field is "name" or "unit"
                ? CatalogId(field == "name" ? trimmed : row.Name,
                    field == "unit" ? trimmed : row.Unit)
                : null;

            using (var update = new SqlCommand(numeric
                ? $"UPDATE dbo.positions SET [{field}]=@value, formulas=@formulas WHERE id=@id"
                : $"UPDATE dbo.positions SET [{field}]=@value, catalog_item_id=@catalog WHERE id=@id",
                conn, tx))
            {
                update.Parameters.Add("@id", SqlDbType.BigInt).Value = row.Id;
                if (numeric)
                {
                    update.Parameters.Add("@value", SqlDbType.Float).Value =
                        number is null ? DBNull.Value : (object)(double)number.Value;
                    update.Parameters.Add("@formulas", SqlDbType.NVarChar, -1).Value = formulas!;
                }
                else
                {
                    update.Parameters.Add("@value", SqlDbType.NVarChar, field == "unit" ? 32 : -1).Value = trimmed;
                    update.Parameters.Add("@catalog", SqlDbType.BigInt).Value =
                        catalogId is null ? DBNull.Value : (object)catalogId.Value;
                }
                if (update.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException($"Позиция {row.Id} изменилась во время сохранения.");
            }
            if (field is "price" or "work_price")
            {
                // Signed acts remain untouched; unsigned acts mirror contract price.
                using var acts = new SqlCommand(
                    $"UPDATE dbo.act_lines SET [{field}]=@v WHERE position_id=@id " +
                    "AND act_id IN (SELECT id FROM dbo.acts WHERE signed=0)", conn, tx);
                acts.Parameters.Add("@v", SqlDbType.Float).Value = (double)(number ?? 0m);
                acts.Parameters.Add("@id", SqlDbType.BigInt).Value = row.Id;
                acts.ExecuteNonQuery();
                if (field == "work_price")
                    foreach (var id in TouchedUnsignedActs(conn, tx, row.Id, contractId))
                        touchedActs.Add(id);
            }

            var before = numeric ? previousNumber?.ToString(CultureInfo.InvariantCulture) ?? "" : previousText;
            var after = numeric ? number?.ToString(CultureInfo.InvariantCulture) ?? "" : trimmed;
            LogPositionChange(conn, tx, row.Id, contractId, objectId, "update",
                $"{docTitle} / {row.SystemName} / {row.Name}: {field}: «{before}» → «{after}» (группа)");
            changed++;
        }

        if (changed == 0)
        {
            tx.Commit();
            return new GroupSaveResult(true, 0, null, revision);
        }

        if (field == "work_price")
            SyncActWork(conn, tx, touchedActs);
        var updatedRev = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new GroupSaveResult(true, changed, null, updatedRev);
    }
}
