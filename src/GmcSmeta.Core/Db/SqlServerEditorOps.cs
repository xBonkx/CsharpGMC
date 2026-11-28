using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Операции редактора сметы на SQL Server — перенос builder-хвостов (acts/builder):
/// скидка на всю смету (apply_discount), массовая цена работ (set_work_selected
/// массовый вариант), сортировка позиций (sort_positions), перенос позиций в раздел
/// (positions_to_sections). Всё — Serializable + meta.rev + пилот-гвард.
/// </summary>
public static class SqlServerEditorOps
{
    private const decimal Eps = 0.000001m;

    /// <summary>
    /// Скидка на всю смету (builder.apply_discount): все клиентские цены × (1 − %/100);
    /// отрицательный процент — наценка. При СМР «по поз.» и флаге smr снижаются и цены
    /// работ; «сумма» — smr_amount, «по системам» — суммы систем. Журнал contract/discount.
    /// </summary>
    public static SqlServerDatabase.SaveResult ApplyDiscount(string connectionString, long contractId,
        decimal percent, bool smr, bool wholeRubles, string expectedRevision)
    {
        RequirePilot(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови смету.", rev);

        string smrMode = "sum", title = "";
        bool locked;
        long objectId;
        using (var read = new SqlCommand(
            "SELECT smr_mode, locked, object_id, name FROM dbo.contracts WHERE id=@id", conn, tx))
        {
            read.Parameters.AddWithValue("@id", contractId);
            using var r = read.ExecuteReader();
            if (!r.Read()) return SqlServerDatabase.SaveResult.Fail("Документ не найден.", rev);
            smrMode = r.IsDBNull(0) ? "sum" : r.GetString(0);
            locked = !r.IsDBNull(1) && SqlFlag.Read(r, 1);
            objectId = SqlInteger.ReadInt64(r, 2);
            title = r.IsDBNull(3) ? "" : r.GetString(3);
        }
        if (locked)
            return SqlServerDatabase.SaveResult.Fail("Смета подписана — скидка недоступна.", rev);

        if(SqlSchemaCompatibility.HasPreviewColumns(conn))
        {
            using var discount=new SqlCommand("SELECT discount_percent FROM dbo.contracts WHERE id=@id",conn,tx);
            discount.Parameters.Add("@id",SqlDbType.BigInt).Value=contractId;
            if(Convert.ToDouble(discount.ExecuteScalar(),CultureInfo.InvariantCulture)!=0d)
                return SqlServerDatabase.SaveResult.Fail("Сначала снимите итоговую скидку, чтобы не применить две скидки.",rev);
        }
        var k = (double)(1m - percent / 100m);
        int digits = wholeRubles ? 0 : 2;
        decimal Scale(decimal v) => decimal.Round(v * (decimal)k, digits, MidpointRounding.AwayFromZero);

        var changed = 0;
        using (var update = new SqlCommand(
            "UPDATE p SET p.price = CAST(ROUND(p.price * @k, @digits) AS FLOAT) " +
            "FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@cid AND p.parent_id IS NULL AND p.out_of_kp=0 " +
            "AND ISNULL(p.price,0) <> 0", conn, tx))
        {
            update.Parameters.AddWithValue("@k", k);
            update.Parameters.AddWithValue("@digits", digits);
            update.Parameters.AddWithValue("@cid", contractId);
            changed += update.ExecuteNonQuery();
        }
        // Формулы цен устаревают при любой записи цены (update_position).
        ClearFormula(conn, tx, contractId, "price");

        if (smr && smrMode == "pos")
            using (var update = new SqlCommand(
                "UPDATE p SET p.work_price = CAST(ROUND(p.work_price * @k, @digits) AS FLOAT) " +
                "FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
                "WHERE sy.contract_id=@cid AND p.parent_id IS NULL AND p.out_of_kp=0 " +
                "AND ISNULL(p.work_price,0) <> 0", conn, tx))
            {
                update.Parameters.AddWithValue("@k", k);
                update.Parameters.AddWithValue("@digits", digits);
                update.Parameters.AddWithValue("@cid", contractId);
                update.ExecuteNonQuery();
                ClearFormula(conn, tx, contractId, "work_price");
            }

        if (smr && smrMode == "systems")
            using (var update = new SqlCommand(
                "UPDATE sy SET sy.smr_amount = CAST(ROUND(sy.smr_amount * @k, @digits) AS FLOAT) " +
                "FROM dbo.systems sy WHERE sy.contract_id=@cid AND ISNULL(sy.smr_amount,0) <> 0", conn, tx))
            {
                update.Parameters.AddWithValue("@k", k);
                update.Parameters.AddWithValue("@digits", digits);
                update.Parameters.AddWithValue("@cid", contractId);
                update.ExecuteNonQuery();
            }
        else if (smr && smrMode != "percent")
            using (var update = new SqlCommand(
                "UPDATE dbo.contracts SET smr_amount = CAST(ROUND(smr_amount * @k, 2) AS FLOAT) " +
                "WHERE id=@id AND ISNULL(smr_amount,0) <> 0", conn, tx))
            {
                update.Parameters.AddWithValue("@k", k);
                update.Parameters.AddWithValue("@id", contractId);
                update.ExecuteNonQuery();
            }

        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,N'contract',@id,N'discount',@details,@contract,@object)", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id", contractId);
            log.Parameters.AddWithValue("@details",
                $"{title}: скидка {percent.ToString("0.##", CultureInfo.InvariantCulture)}% ({changed} строк)");
            log.Parameters.AddWithValue("@contract", contractId);
            log.Parameters.AddWithValue("@object", objectId);
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>Сброс устаревшей формулы цены у строк сметы (set_formula(f, None)).</summary>
    private static void ClearFormula(SqlConnection conn, SqlTransaction tx, long contractId, string field)
    {
        var updates = new List<(long Id, string Json)>();
        using (var read = new SqlCommand(
            "SELECT p.id, ISNULL(p.formulas,N'') FROM dbo.positions p " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@cid AND p.parent_id IS NULL AND p.out_of_kp=0 " +
            "AND ISNULL(p.formulas,N'') LIKE '%\"' + field + '\"%'", conn, tx))
        {
            read.Parameters.AddWithValue("@cid", contractId);
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                var id = SqlInteger.ReadInt64(r, 0);
                var json = r.GetString(1);
                if (!json.Contains("\"" + field + "\"")) continue;
                try
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
                    if (node is null || !node.Remove(field)) continue;
                    updates.Add((id, node.Count == 0 ? "" : node.ToJsonString()));
                }
                catch (System.Text.Json.JsonException) { }
            }
        }
        foreach (var (id, json) in updates)
            using (var update = new SqlCommand(
                "UPDATE dbo.positions SET formulas=@f WHERE id=@id", conn, tx))
            {
                update.Parameters.AddWithValue("@f", json.Length == 0 ? DBNull.Value : json);
                update.Parameters.AddWithValue("@id", id);
                update.ExecuteNonQuery();
            }
    }

    /// <summary>
    /// Сортировка позиций раздела (builder.sort_positions): «name» — А→Я,
    /// «sum» — дорогие вперёд; комплектующие остаются под своей строкой.
    /// </summary>
    public static SqlServerDatabase.SaveResult SortPositions(string connectionString, long contractId,
        long systemId, string how, string expectedRevision)
    {
        if (how is not ("name" or "sum"))
            return SqlServerDatabase.SaveResult.Fail("Режим сортировки: «name» или «sum».", expectedRevision);
        RequirePilot(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови смету.", rev);

        var orderExpr = how == "name"
            ? "p.name COLLATE Cyrillic_General_CI_AS"
            : "p.qty*ISNULL(p.price,0) DESC";
        using (var renumber = new SqlCommand(
            "WITH tops AS (" +
            "SELECT p.id, ROW_NUMBER() OVER (ORDER BY " + orderExpr + ") - 1 AS n " +
            "FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.id=@sid AND p.parent_id IS NULL), " +
            "kids AS (SELECT p.id, t.n + ROW_NUMBER() OVER (PARTITION BY p.parent_id ORDER BY p.[order],p.id)/1000.0 AS n " +
            "FROM dbo.positions p JOIN tops t ON t.id=p.parent_id) " +
            "UPDATE p SET p.[order]=CAST(x.n AS INT) FROM dbo.positions p " +
            "JOIN (SELECT id,n FROM tops UNION ALL SELECT id,n FROM kids) x ON x.id=p.id", conn, tx))
        {
            renumber.Parameters.AddWithValue("@sid", systemId);
            renumber.ExecuteNonQuery();
        }
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
            "VALUES (@ts,@user,N'system',@id,N'update',@details,@contract)", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id", systemId);
            log.Parameters.AddWithValue("@details", how == "name" ? "сортировка: по названию" : "сортировка: по сумме");
            log.Parameters.AddWithValue("@contract", contractId);
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>Преобразование выделенных строк в разделы (builder.positions_to_sections):
    /// каждая выбранная верхняя строка становится новым разделом с её комплектующими.</summary>
    public static SqlServerDatabase.SaveResult PositionsToSections(string connectionString,
        long contractId, long sourceSystemId, IReadOnlyList<long> positionIds, string expectedRevision)
    {
        RequirePilot(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови смету.", rev);

        foreach (var positionId in positionIds)
        {
            string name;
            long? parentId;
            using (var read = new SqlCommand(
                "SELECT name, parent_id FROM dbo.positions WHERE id=@id AND system_id=@sys", conn, tx))
            {
                read.Parameters.AddWithValue("@id", positionId);
                read.Parameters.AddWithValue("@sys", sourceSystemId);
                using var r = read.ExecuteReader();
                if (!r.Read()) continue;
                name = r.IsDBNull(0) ? "" : r.GetString(0);
                parentId = r.IsDBNull(1) ? null : SqlInteger.ReadInt64(r, 1);
                if (parentId is not null) continue;   // только верхние строки
            }
            // Новая система после текущего раздела; позиция переезжает в неё верхом.
            using (var createSystem = new SqlCommand(
                "INSERT dbo.systems (contract_id,parent_id,name,[order],smr_amount,heading,source_num) " +
                "OUTPUT INSERTED.id " +
                "SELECT contract_id, NULL, @name, [order] + 1, NULL, N'', N'' " +
                "FROM dbo.systems WHERE id=@sys", conn, tx))
            {
                createSystem.Parameters.AddWithValue("@name", name);
                createSystem.Parameters.AddWithValue("@sys", sourceSystemId);
                var newSystemId = SqlInteger.RequireInt64(createSystem.ExecuteScalar());
                using (var move = new SqlCommand(
                    "UPDATE p SET p.system_id=@newSys, p.parent_id=NULL " +
                    "FROM dbo.positions p WHERE p.id=@pid OR p.parent_id=@pid", conn, tx))
                {
                    move.Parameters.AddWithValue("@newSys", newSystemId);
                    move.Parameters.AddWithValue("@pid", positionId);
                    move.ExecuteNonQuery();
                }
            }
        }
        // Порядок разделов уплотняется один раз после всех переносов.
        using (var renumber = new SqlCommand(
            "WITH ord AS (SELECT id, ROW_NUMBER() OVER (ORDER BY [order],id)-1 AS n " +
            "FROM dbo.systems WHERE contract_id=@cid) " +
            "UPDATE s SET s.[order]=CAST(ord.n AS INT) FROM dbo.systems s JOIN ord ON ord.id=s.id " +
            "WHERE s.contract_id=@cid", conn, tx))
        {
            renumber.Parameters.AddWithValue("@cid", contractId);
            renumber.ExecuteNonQuery();
        }
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
            "VALUES (@ts,@user,N'system',0,N'update',@details,@contract)", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@details", $"позиции преобразованы в разделы: {positionIds.Count}");
            log.Parameters.AddWithValue("@contract", contractId);
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    private static void RequirePilot(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!builder.InitialCatalog.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Запись C# разрешена только в отдельную тестовую базу GmcSmetaPilot_*. Рабочая база не изменена.");
    }
}
