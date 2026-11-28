using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerCostOperations
{
    /// <summary>
    /// Apply supplier to all selected materials using one revision, one transaction.
    /// Implemented in the cost module; procurement panels remain owned by P0 #1.
    /// </summary>
    public static BatchResult SetSupplierBatch(string connectionString, int contractId,
        IReadOnlyList<long> positionIds, string name, string expectedRevision)
    {
        if (name.Length > 255) throw new ArgumentException("Слишком длинное имя поставщика.");
        var ids = positionIds.Where(x => x > 0).Distinct().ToArray();
        if (ids.Length == 0) return new BatchResult(0, 0, expectedRevision);
        using var conn = OpenWrite(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = LockRevision(conn, tx, expectedRevision);
        var sources = Sources(conn, tx, contractId, ids);
        if (sources.Count != ids.Length)
            throw new InvalidOperationException("Не все позиции принадлежат договору; изменения отменены.");

        var supplierName = name.Trim();
        long? supplierId = null;
        if (supplierName.Length != 0)
        {
            using (var read = new SqlCommand(
                "SELECT TOP(1) id FROM dbo.suppliers WITH(UPDLOCK,HOLDLOCK) WHERE name=@name",
                conn, tx))
            {
                read.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = supplierName;
                var found = read.ExecuteScalar();
                if (found is not null and not DBNull)
                    supplierId = Convert.ToInt64(found, CultureInfo.InvariantCulture);
            }
            if (supplierId is null)
            {
                using var create = new SqlCommand(
                    "INSERT dbo.suppliers(name, note) OUTPUT inserted.id VALUES(@name, N'')", conn, tx);
                create.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = supplierName;
                supplierId = Convert.ToInt64(create.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
        var changed = 0;
        foreach (var chunk in ids.Chunk(400))
        {
            using var cmd = new SqlCommand { Connection = conn, Transaction = tx };
            var names = new List<string>();
            for (var i = 0; i < chunk.Length; i++)
            {
                var n = "@p" + i.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.Add(n, SqlDbType.BigInt).Value = chunk[i];
                names.Add(n);
            }
            cmd.Parameters.Add("@supplier", SqlDbType.BigInt).Value =
                supplierId is null ? DBNull.Value : (object)supplierId.Value;
            cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
            var csv = string.Join(",", names);
            cmd.CommandText =
                "UPDATE pr SET supplier_id=@supplier,updated_at=SYSDATETIME(),updated_by=@user " +
                "FROM dbo.procurement pr JOIN dbo.positions p ON p.id=pr.position_id " +
                "JOIN dbo.systems sy ON sy.id=p.system_id " +
                "WHERE sy.contract_id=@cid AND pr.position_id IN (" + csv + ") " +
                "AND (ISNULL(pr.supplier_id,-1)<>ISNULL(@supplier,-1)); " +
                "INSERT dbo.procurement (position_id,supplier_id,source,stage,order_ref,invoice_ref," +
                "responsible,replacement,direct_to_site,note,buy_name,updated_at,updated_by) " +
                "SELECT p.id,@supplier,N'buy',N'plan',N'',N'',N'',N'',0,N'',p.name,SYSDATETIME(),@user " +
                "FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
                "WHERE sy.contract_id=@cid AND p.id IN (" + csv + ") " +
                "AND NOT EXISTS(SELECT 1 FROM dbo.procurement pr WITH(UPDLOCK,HOLDLOCK) " +
                "WHERE pr.position_id=p.id);";
            cmd.Parameters.Add("@cid", SqlDbType.BigInt).Value = contractId;
            // SQL row counts of a multi-statement command include both update and insert.
            changed += cmd.ExecuteNonQuery();
        }
        if (changed != 0)
        {
            Log(conn, tx, contractId, "contract", contractId, "update",
                "Поставщик «" + supplierName + "» → " + changed + " поз.");
            rev = SqlServerDatabase.BumpPositionRevision(conn, tx);
        }
        tx.Commit();
        return new BatchResult(changed, 0, rev);
    }

    /// <summary>
    /// Clear selected cells in the material cost tab. Restricts to the same
    /// contract, preserves invoice cost, synchronizes only unsigned KS prices.
    /// </summary>
    public static BatchResult ClearMaterialCells(string connectionString, int contractId,
        IReadOnlyList<long> positionIds, string field, string expectedRevision)
    {
        if (field is not ("price" or "purchase_price" or "cost_price" or "supplier" or "cost_note"))
            throw new ArgumentException("Недопустимое поле очистки.", nameof(field));
        if (positionIds.Any(id => id <= 0))
            throw new ArgumentException("Некорректный ID позиции.", nameof(positionIds));
        var ids = positionIds.Distinct().ToArray();
        if (ids.Length == 0) return new BatchResult(0, 0, expectedRevision);
        using var conn = OpenWrite(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = LockRevision(conn, tx, expectedRevision);
        var changed = 0;
        var skipped = 0;
        foreach (var id in ids)
        {
            // Include components for non-retail fields, but never cross a document
            // boundary or change an out-of-estimate row from this material view.
            bool locked, sub, work;
            string formulas;
            using (var read = new SqlCommand(
                "SELECT c.locked,p.parent_id,p.name,p.formulas FROM dbo.positions p WITH(UPDLOCK,HOLDLOCK) " +
                "JOIN dbo.systems sy ON sy.id=p.system_id JOIN dbo.contracts c ON c.id=sy.contract_id " +
                "WHERE p.id=@id AND sy.contract_id=@contract AND ISNULL(p.out_of_kp,0)=0", conn, tx))
            {
                read.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                read.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
                using var reader = read.ExecuteReader();
                if (!reader.Read())
                    throw new DBConcurrencyException("Позиция не принадлежит смете; очистка отменена.");
                locked = !reader.IsDBNull(0) && SqlFlag.Read(reader, 0);
                sub = !reader.IsDBNull(1);
                work = GmcSmeta.Core.Domain.EstimateStatistics.IsWorkName(reader.IsDBNull(2) ? "" : reader.GetString(2));
                formulas = reader.IsDBNull(3) ? "" : reader.GetString(3);
            }
            if (field == "price" && locked)
                throw new InvalidOperationException("Подписанная смета: цену заказчику очищать нельзя.");
            // Same locked-document policy as the existing SetProc fact editor.
            if (field == "cost_price" && locked)
                throw new InvalidOperationException("Подписанный документ: факт по счёту недоступен для изменения.");
            if (field == "price" && (sub || work))
            {
                skipped++;
                continue;
            }
            int affected;
            if (field is "cost_price" or "supplier")
            {
                // No new procurement records for an already empty field.
                var column = field == "supplier" ? "supplier_id" : "cost_price";
                using var cmd = new SqlCommand(
                    "UPDATE dbo.procurement SET [" + column + "]=NULL,updated_at=SYSDATETIME(),updated_by=@user " +
                    "WHERE position_id=@id AND [" + column + "] IS NOT NULL", conn, tx);
                cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
                affected = cmd.ExecuteNonQuery();
            }
            else if (field == "cost_note")
            {
                using var cmd = new SqlCommand(
                    "UPDATE dbo.positions SET cost_note=N'' WHERE id=@id AND ISNULL(cost_note,N'')<>N''", conn, tx);
                cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                affected = cmd.ExecuteNonQuery();
            }
            else
            {
                // Preserve the original JSON if this column had no formula.
                // Already-empty cells do not cause an otherwise needless write.
                var newFormula = formulas;
                if (!string.IsNullOrWhiteSpace(formulas))
                {
                    var parsed = System.Text.Json.Nodes.JsonNode.Parse(formulas)
                        as System.Text.Json.Nodes.JsonObject ??
                        throw new FormatException("Формулы позиции должны быть объектом JSON.");
                    if (parsed.ContainsKey(field)) newFormula = RemoveFormula(formulas, field);
                }
                using var cmd = new SqlCommand(
                    "UPDATE dbo.positions SET [" + field + "]=@v,formulas=@json WHERE id=@id AND (" +
                    (field == "price" ? "ISNULL(price,0)<>0" : "purchase_price IS NOT NULL") +
                    " OR ISNULL(formulas,N'')<>@json)", conn, tx);
                cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                cmd.Parameters.Add("@v", SqlDbType.Float).Value = field == "price" ? (object)0d : DBNull.Value;
                cmd.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value = newFormula;
                affected = cmd.ExecuteNonQuery();
                if (affected > 0 && field == "price")
                {
                    using var acts = new SqlCommand(
                        "UPDATE dbo.act_lines SET price=0 WHERE position_id=@id AND act_id IN " +
                        "(SELECT id FROM dbo.acts WHERE contract_id=@contract AND signed=0)", conn, tx);
                    acts.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                    acts.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
                    acts.ExecuteNonQuery();
                }
            }
            if (affected > 0) changed++;
            else skipped++;
        }
        if (changed > 0)
        {
            Log(conn, tx, contractId, "contract", contractId, "update",
                "Очищено «" + field + "»: " + changed + " поз.");
            rev = SqlServerDatabase.BumpPositionRevision(conn, tx);
        }
        tx.Commit();
        return new BatchResult(changed, skipped, rev);
    }

}
