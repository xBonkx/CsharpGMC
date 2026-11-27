using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Cost-only operations from cost_view / smr_view. No editor or procurement
/// implementation is replaced. Every write is contract-scoped and revisioned.
/// </summary>
public static partial class SqlServerCostOperations
{
    public sealed record BatchResult(int Changed, int Skipped, string Revision);

    /// <summary>Python round(value, 2): midpoint-to-even, not AwayFromZero.</summary>
    public static decimal PriceFromMarkup(decimal cost, decimal percent, bool wholeRubles = false)
    {
        if (percent < -100m || percent > 10000m)
            throw new ArgumentOutOfRangeException(nameof(percent));
        return (decimal)FinanceReader.PythonRound(
            (double)cost * (1d + (double)percent / 100d), wholeRubles ? 0 : 2);
    }

    public static decimal PriceFromFactor(decimal cost, decimal factor, bool wholeRubles = false)
    {
        if (factor <= 0m || factor > 101m)
            throw new ArgumentOutOfRangeException(nameof(factor));
        return (decimal)FinanceReader.PythonRound((double)cost * (double)factor, wholeRubles ? 0 : 2);
    }

    public static decimal CostFromRetail(decimal retail, decimal factor)
    {
        if (factor <= 0m || factor > 101m)
            throw new ArgumentOutOfRangeException(nameof(factor));
        return (decimal)FinanceReader.PythonRound((double)retail / (double)factor, 2);
    }

    public static decimal TransportFromSuppliers(IEnumerable<decimal> supplierTotals, decimal percent)
    {
        if (percent < 0m || percent > 10000m)
            throw new ArgumentOutOfRangeException(nameof(percent));
        double sum = 0d;
        foreach (var total in supplierTotals) sum += (double)total;
        var baseAmount = FinanceReader.PythonRound(sum, 2);
        return (decimal)FinanceReader.PythonRound(baseAmount * (double)percent / 100d, 2);
    }

    private static SqlConnection OpenWrite(string connectionString)
    {
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (!database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Изменения разрешены только в изолированной GmcSmetaPilot_*.");
        return SqlServerDatabase.Open(connectionString);
    }

    private static string LockRevision(SqlConnection conn, SqlTransaction tx, string expectedRevision)
    {
        var revision = SqlServerDatabase.LockRevision(conn, tx)
            ?? throw new InvalidOperationException("Отсутствует meta.rev.");
        if (!string.Equals(revision, expectedRevision, StringComparison.Ordinal))
            throw new DBConcurrencyException("Ревизия изменена другим сотрудником. Обновите себестоимость.");
        return revision;
    }

    private static void Log(SqlConnection conn, SqlTransaction tx, long contractId,
        string entity, long entityId, string action, string details)
    {
        using var log = new SqlCommand(
            "INSERT INTO dbo.change_log(ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES(SYSDATETIME(),@user,@entity,@eid,@action,@details,@contract, " +
            "(SELECT object_id FROM dbo.contracts WHERE id=@contract))", conn, tx);
        log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
        log.Parameters.Add("@entity", SqlDbType.NVarChar, 40).Value = entity;
        log.Parameters.Add("@eid", SqlDbType.BigInt).Value = entityId;
        log.Parameters.Add("@action", SqlDbType.NVarChar, 40).Value = action;
        log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = details;
        log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        log.ExecuteNonQuery();
    }

    private sealed record PriceSource(long Id, decimal? Planned, decimal? Fact,
        decimal Retail, string Formulas, bool Locked, bool IsWork);

    private static List<PriceSource> Sources(SqlConnection conn, SqlTransaction tx,
        long contractId, IReadOnlyList<long> ids)
    {
        var result = new List<PriceSource>();
        foreach (var chunk in ids.Distinct().Chunk(400))
        {
            using var read = new SqlCommand { Connection = conn, Transaction = tx };
            var parameters = new List<string>();
            for (var i = 0; i < chunk.Length; i++)
            {
                var name = "@p" + i.ToString(CultureInfo.InvariantCulture);
                parameters.Add(name);
                read.Parameters.Add(name, SqlDbType.BigInt).Value = chunk[i];
            }
            read.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            read.CommandText =
                "SELECT p.id,p.purchase_price,pr.cost_price,p.price,p.formulas,c.locked,p.name " +
                "FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
                "JOIN dbo.systems sy ON sy.id=p.system_id " +
                "JOIN dbo.contracts c ON c.id=sy.contract_id " +
                "LEFT JOIN dbo.procurement pr ON pr.position_id=p.id " +
                "WHERE sy.contract_id=@contract AND p.parent_id IS NULL " +
                "AND ISNULL(p.out_of_kp,0)=0 AND p.id IN (" + string.Join(",", parameters) + ")";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                decimal? Dec(int i) => reader.IsDBNull(i) ? null :
                    Convert.ToDecimal(reader.GetValue(i), CultureInfo.InvariantCulture);
                var name = reader.IsDBNull(6) ? "" : reader.GetString(6);
                result.Add(new PriceSource(
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Dec(1), Dec(2), Dec(3) ?? 0m,
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    !reader.IsDBNull(5) && SqlFlag.Read(reader, 5),
                    GmcSmeta.Core.Domain.EstimateStatistics.IsWorkName(name)));
            }
        }
        return result;
    }

    private static string RemoveFormula(string json, string field)
    {
        JsonObject formulas;
        try
        {
            formulas = string.IsNullOrWhiteSpace(json) ? new JsonObject() :
                JsonNode.Parse(json) as JsonObject
                ?? throw new FormatException("Формулы позиции должны быть объектом JSON.");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException("Нельзя безопасно обновить формулы: " + e.Message, e);
        }
        formulas.Remove(field);
        return formulas.ToJsonString();
    }

    /// <summary>
    /// Material markup from the selected top-level rows (cost_view.markup_dialog).
    /// The position's actual invoice cost takes precedence over planned purchase.
    /// The counterpart remains in the estimate and unsigned act_lines.
    /// </summary>
    public static BatchResult MarkupMaterials(string connectionString, int contractId,
        IReadOnlyList<long> positionIds, decimal factor, bool priceFromCost,
        bool onlyEmpty, bool wholeRubles, string expectedRevision)
    {
        // Validate the coefficient before opening any connection or transaction.
        if (factor <= 0m || factor > 101m)
            throw new ArgumentOutOfRangeException(nameof(factor));
        if (positionIds.Count == 0) return new BatchResult(0, 0, expectedRevision);
        using var conn = OpenWrite(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = LockRevision(conn, tx, expectedRevision);
        var sources = Sources(conn, tx, contractId, positionIds);
        var skipped = positionIds.Distinct().Count() - sources.Count;
        var changed = 0;
        foreach (var row in sources)
        {
            if (row.Locked && priceFromCost)
                throw new InvalidOperationException("Подписанная смета: цену клиенту нельзя менять.");
            if (row.IsWork && priceFromCost) { skipped++; continue; }
            var cost = row.Fact ?? row.Planned;
            if (priceFromCost)
            {
                if (cost is null || (onlyEmpty && row.Retail != 0m)) { skipped++; continue; }
                var price = PriceFromFactor(cost.Value, factor, wholeRubles);
                if (row.Retail == price) continue;
                using (var update = new SqlCommand(
                    "UPDATE dbo.positions SET price=@v,formulas=@formulas WHERE id=@id", conn, tx))
                {
                    update.Parameters.Add("@v", SqlDbType.Float).Value = (double)price;
                    update.Parameters.Add("@formulas", SqlDbType.NVarChar, -1).Value =
                        RemoveFormula(row.Formulas, "price");
                    update.Parameters.Add("@id", SqlDbType.BigInt).Value = row.Id;
                    if (update.ExecuteNonQuery() != 1)
                        throw new DBConcurrencyException("Позиция удалена.");
                }
                using (var acts = new SqlCommand(
                    "UPDATE dbo.act_lines SET price=@v WHERE position_id=@id AND act_id IN " +
                    "(SELECT id FROM dbo.acts WHERE contract_id=@contract AND signed=0)", conn, tx))
                {
                    acts.Parameters.Add("@v", SqlDbType.Float).Value = (double)price;
                    acts.Parameters.Add("@id", SqlDbType.BigInt).Value = row.Id;
                    acts.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
                    acts.ExecuteNonQuery();
                }
            }
            else
            {
                if (row.Retail == 0m || (onlyEmpty && row.Planned is not null))
                {
                    skipped++; continue;
                }
                var price = CostFromRetail(row.Retail, factor);
                if (row.Planned == price) continue;
                using var update = new SqlCommand(
                    "UPDATE dbo.positions SET purchase_price=@v,formulas=@formulas WHERE id=@id", conn, tx);
                update.Parameters.Add("@v", SqlDbType.Float).Value = (double)price;
                update.Parameters.Add("@formulas", SqlDbType.NVarChar, -1).Value =
                    RemoveFormula(row.Formulas, "purchase_price");
                update.Parameters.Add("@id", SqlDbType.BigInt).Value = row.Id;
                if (update.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Позиция удалена.");
            }
            changed++;
            Log(conn, tx, contractId, "position", row.Id, "update",
                "Себестоимость: массовое изменение " + (priceFromCost ? "розницы" : "закупа"));
        }
        var next = changed > 0 ? SqlServerDatabase.BumpPositionRevision(conn, tx) : revision;
        tx.Commit();
        return new BatchResult(changed, skipped, next);
    }

    /// <summary>Insert catalog purchase prices only for missing entries, never automatically.</summary>
    public static BatchResult FillPurchaseFromCatalog(string connectionString, int contractId,
        string expectedRevision)
    {
        using var conn = OpenWrite(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = LockRevision(conn, tx, expectedRevision);
        // Python builder.catalog_entries: latest position with a positive retail price,
        // not price_history. Do not choose an earlier non-empty purchase by mistake.
        var changes = new List<(long Id, decimal Cost, string Formulas)>();
        using (var read = new SqlCommand(@"
WITH last_price AS
 (SELECT catalog_item_id,purchase_price,
         ROW_NUMBER() OVER(PARTITION BY catalog_item_id ORDER BY id DESC) AS seq
  FROM dbo.positions WHERE catalog_item_id IS NOT NULL AND price>0)
SELECT p.id,lp.purchase_price,p.formulas
FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK)
JOIN dbo.systems sy ON sy.id=p.system_id
JOIN last_price lp ON lp.catalog_item_id=p.catalog_item_id AND lp.seq=1
WHERE sy.contract_id=@id AND p.purchase_price IS NULL
  AND ISNULL(p.out_of_kp,0)=0 AND lp.purchase_price IS NOT NULL AND lp.purchase_price<>0",
            conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = read.ExecuteReader();
            while (r.Read())
                changes.Add((
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(r.GetValue(1), CultureInfo.InvariantCulture),
                    r.IsDBNull(2) ? "" : r.GetString(2)));
        }
        foreach (var (id, cost, formulas) in changes)
        {
            using var update = new SqlCommand(
                "UPDATE dbo.positions SET purchase_price=@cost,formulas=@formulas " +
                "WHERE id=@id AND purchase_price IS NULL", conn, tx);
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            update.Parameters.Add("@cost", SqlDbType.Float).Value = (double)cost;
            update.Parameters.Add("@formulas", SqlDbType.NVarChar, -1).Value =
                RemoveFormula(formulas, "purchase_price");
            if (update.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Закуп уже обновлён. Повторите после перезагрузки.");
            Log(conn, tx, contractId, "position", id, "update", "Закуп из каталога: " + cost);
        }
        var next = changes.Count > 0 ? SqlServerDatabase.BumpPositionRevision(conn, tx) : revision;
        tx.Commit();
        return new BatchResult(changes.Count, 0, next);
    }

    /// <summary>Transport cost and sale are distinct contract columns, not part of position totals.</summary>
    public static SqlServerDatabase.SaveResult SaveTransport(string connectionString, int contractId,
        decimal? ourCost, decimal customerAmount, string note, string expectedRevision)
    {
        if (ourCost < 0m || customerAmount < 0m)
            return SqlServerDatabase.SaveResult.Fail("Транспорт не может иметь отрицательную сумму.", expectedRevision);
        using var conn = OpenWrite(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = LockRevision(conn, tx, expectedRevision);
        bool locked;
        decimal oldCustomer;
        using (var read = new SqlCommand(
            "SELECT locked,ISNULL(transport_amount,0) FROM dbo.contracts WITH(UPDLOCK,HOLDLOCK) WHERE id=@id",
            conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = read.ExecuteReader();
            if (!r.Read()) return SqlServerDatabase.SaveResult.Fail("Договор не найден.", rev);
            locked = SqlFlag.Read(r, 0);
            oldCustomer = Convert.ToDecimal(r.GetValue(1), CultureInfo.InvariantCulture);
        }
        if (locked && customerAmount != oldCustomer)
            return SqlServerDatabase.SaveResult.Fail("Подписанный договор: сумму клиенту менять нельзя.", rev);
        using (var cmd = new SqlCommand(
            "UPDATE dbo.contracts SET transport_cost=@cost, transport_amount=@sell, transport_note=@note " +
            "WHERE id=@id", conn, tx))
        {
            cmd.Parameters.Add("@cost", SqlDbType.Float).Value =
                ourCost is null ? DBNull.Value : (object)(double)ourCost.Value;
            cmd.Parameters.Add("@sell", SqlDbType.Float).Value = (double)customerAmount;
            cmd.Parameters.Add("@note", SqlDbType.NVarChar, -1).Value = note;
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            if (cmd.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Договор исчез при сохранении транспорта.");
        }
        Log(conn, tx, contractId, "contract", contractId, "update", "Транспорт: клиенту / нам");
        var next = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, next);
    }

    public static (decimal? Cost, decimal Customer, string Note) ReadTransport(
        string connectionString, int contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        using var read = new SqlCommand(
            "SELECT transport_cost,transport_amount,transport_note FROM dbo.contracts WHERE id=@id", conn);
        read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var r = read.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Договор не найден.");
        return (
            r.IsDBNull(0) ? null : Convert.ToDecimal(r.GetValue(0), CultureInfo.InvariantCulture),
            r.IsDBNull(1) ? 0m : Convert.ToDecimal(r.GetValue(1), CultureInfo.InvariantCulture),
            r.IsDBNull(2) ? "" : r.GetString(2));
    }

    public static BatchResult SetCostFormatting(string connectionString, int contractId,
        IReadOnlyList<long> ids, string field, string value, string expectedRevision)
    {
        if (field is not ("cost_color" or "cost_note"))
            throw new ArgumentException("Разрешены только цвет и внутренние пометки.", nameof(field));
        if (field == "cost_color" && value.Length != 0 &&
            (!System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$")))
            throw new ArgumentException("Цвет должен иметь вид #RRGGBB.");
        if (ids.Count == 0) return new BatchResult(0, 0, expectedRevision);
        using var conn = OpenWrite(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = LockRevision(conn, tx, expectedRevision);
        var sources = Sources(conn, tx, contractId, ids);
        var changed = 0;
        foreach (var row in sources)
        {
            using var update = new SqlCommand(
                "UPDATE dbo.positions SET [" + field + "]=@v WHERE id=@id", conn, tx);
            update.Parameters.Add("@v", SqlDbType.NVarChar, field == "cost_color" ? 16 : -1).Value = value;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = row.Id;
            if (update.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Позиция исчезла при форматировании.");
            changed++;
        }
        if (changed > 0)
            Log(conn, tx, contractId, "contract", contractId, "update",
                field + ": " + changed.ToString(CultureInfo.InvariantCulture) + " строк");
        var next = changed > 0 ? SqlServerDatabase.BumpPositionRevision(conn, tx) : rev;
        tx.Commit();
        return new BatchResult(changed, ids.Distinct().Count() - sources.Count, next);
    }
}
