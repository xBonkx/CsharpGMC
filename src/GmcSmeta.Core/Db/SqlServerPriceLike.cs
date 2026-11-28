using System.Data;
using System.Globalization;
using System.Text.Json;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Price-like operation: all eligible identical rows are saved in ONE
/// SERIALIZABLE SQL Server transaction, preserving act prices, source price,
/// unrelated formulas and a single meta.rev. No per-row Open/SaveCell loop.
/// Pilot-only until real integration tests pass on GmcSmetaPilot_*.
/// </summary>
public static class SqlServerPriceLike
{
    public sealed record PriceLikeResult(bool Saved, int Changed, string? Error, string Revision)
    {
        public static PriceLikeResult Fail(string error, string revision) =>
            new(false, 0, error, revision);
    }

    private static List<PriceLikeBatchPlan.Position> RowsOf(
        SqlConnection conn, long contractId, SqlTransaction? transaction = null)
    {
        var rows = new List<PriceLikeBatchPlan.Position>();
        var hint = transaction is null ? "" : " WITH (UPDLOCK,HOLDLOCK)";
        using var cmd = new SqlCommand(
            "SELECT p.id,p.name,p.unit,p.parent_id,p.out_of_kp," +
            "p.price,p.purchase_price,p.formulas " +
            $"FROM dbo.positions p{hint} JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@contract ORDER BY sy.[order],sy.id,p.[order],p.id",
            conn, transaction);
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new PriceLikeBatchPlan.Position(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3),
                !reader.IsDBNull(4) &&
                    Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? 0m : Money.FromDouble(
                    Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture)),
                reader.IsDBNull(6) ? null : Money.FromDouble(
                    Convert.ToDouble(reader.GetValue(6), CultureInfo.InvariantCulture)),
                reader.IsDBNull(7) ? "" : reader.GetString(7)));
        }
        return rows;
    }

    public static int SamePriceCount(string connectionString, long contractId, long positionId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        var rows = RowsOf(conn, contractId);
        var source = rows.FirstOrDefault(r => r.Id == positionId);
        if (source is null || !source.IsRoot || string.IsNullOrWhiteSpace(source.Name))
            return 0;
        var key = Catalog.Key(source.Name, source.Unit);
        return rows.Count(r => r.Id != source.Id && r.IsRoot && !r.Outside &&
            Catalog.Key(r.Name, r.Unit) == key && r.Price != source.Price);
    }

    public static PriceLikeResult SetPriceLike(string connectionString,
        long contractId, long positionId, string field, string expectedRevision)
    {
        if (contractId <= 0 || positionId <= 0 || string.IsNullOrWhiteSpace(expectedRevision) ||
            field is not ("price" or "purchase_price"))
            return PriceLikeResult.Fail("Некорректный документ, позиция или колонка цены.",
                expectedRevision);

        // This new set-based writer must be verified on the pilot SQL Server
        // before enabling it for other databases.
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (!SqlSchemaCompatibility.IsWritablePilot(builder.InitialCatalog, builder.DataSource))
                return PriceLikeResult.Fail(
                    "Массовое копирование цены пока доступно только в GmcSmetaPilot_*.",
                    expectedRevision);
        }
        catch (ArgumentException)
        {
            return PriceLikeResult.Fail("Некорректная строка подключения SQL Server.",
                expectedRevision);
        }

        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = SqlServerDatabase.LockRevision(conn, tx);
        if (revision is null)
            return PriceLikeResult.Fail("На сервере отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(revision, expectedRevision, StringComparison.Ordinal))
            return PriceLikeResult.Fail(
                "Смета изменилась у другого сотрудника. Обнови её.", revision);

        long objectId;
        string contractName;
        bool locked;
        using (var cmd = new SqlCommand(
            "SELECT object_id,name,locked FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return PriceLikeResult.Fail("Документ уже удалён.", revision);
            objectId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            contractName = reader.IsDBNull(1) ? "" : reader.GetString(1);
            locked = !reader.IsDBNull(2) &&
                Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture);
        }
        // This bulk command is disabled for signed estimates in the editor.
        // Ordinary individual procurement price editing uses a separate writer.
        if (locked)
            return PriceLikeResult.Fail(
                "Документ подписан — массовый перенос цены недоступен.", revision);

        // All rows are read under the same revision lock before any write.
        // Input names/units are normalized in .NET exactly as for source rows.
        PriceLikeBatchPlan.Plan plan;
        try
        {
            plan = PriceLikeBatchPlan.Build(RowsOf(conn, contractId, tx), positionId, field);
        }
        catch (InvalidOperationException ex)
        {
            return PriceLikeResult.Fail(ex.Message, revision);
        }

        if (plan.TargetIds.Count == 0)
        {
            tx.Commit();
            return new PriceLikeResult(true, 0, null, revision);
        }

        var amount = plan.Price is decimal value ? (object)(double)value : DBNull.Value;
        var priceText = plan.Price?.ToString(CultureInfo.InvariantCulture) ?? "";
        var idsJson = JsonSerializer.Serialize(plan.TargetIds);

        // SQL field and JSON path are hard-coded by the whitelist above.
        // OPENJSON permits one UPDATE for thousands of rows and an exact count
        // check. A mismatch throws and rolls back *all* values/acts/change log.
        var sql = $@"
            DECLARE @updated TABLE (position_id BIGINT PRIMARY KEY, old_price FLOAT NULL);
            UPDATE p SET [{field}]=@value,
                formulas=JSON_MODIFY(
                    CASE WHEN p.formulas IS NULL OR LTRIM(RTRIM(p.formulas))=N''
                         THEN N'{{}}' ELSE p.formulas END,
                    '$.{field}', NULL)
            OUTPUT inserted.id, deleted.[{field}]
            INTO @updated(position_id,old_price)
            FROM dbo.positions p
            JOIN dbo.systems sy ON sy.id=p.system_id
            JOIN OPENJSON(@ids) src
                ON TRY_CONVERT(BIGINT, src.[value])=p.id
            WHERE sy.contract_id=@contract AND p.parent_id IS NULL
              AND p.out_of_kp=0;
            IF (SELECT COUNT(1) FROM @updated) <> @expected
                THROW 51007, 'Изменился состав одинаковых позиций; откат пакета.', 1;
            {(field == "price" ? @"UPDATE al SET price=@value
                FROM dbo.act_lines al
                JOIN dbo.acts a ON a.id=al.act_id
                JOIN @updated u ON u.position_id=al.position_id
                WHERE a.signed=0;" : "")}
            INSERT INTO dbo.change_log
                (ts,[user],entity,entity_id,action,details,contract_id,object_id)
            SELECT SYSDATETIME(),@username,N'position',u.position_id,N'update',
                CONCAT(@doc,N' / ',sy.name,N' / ',p.name,
                    N': {field}: «',COALESCE(CONVERT(NVARCHAR(50),u.old_price),N''),
                    N'» → «',@valueText,N'» (одинаковые)'),
                @contract,@object
            FROM @updated u
            JOIN dbo.positions p ON p.id=u.position_id
            JOIN dbo.systems sy ON sy.id=p.system_id;
            SELECT COUNT(1) FROM @updated;";

        using (var command = new SqlCommand(sql, conn, tx) { CommandTimeout = 90 })
        {
            command.Parameters.Add("@ids", SqlDbType.NVarChar, -1).Value = idsJson;
            command.Parameters.Add("@expected", SqlDbType.Int).Value = plan.TargetIds.Count;
            command.Parameters.Add("@value", SqlDbType.Float).Value = amount;
            command.Parameters.Add("@valueText", SqlDbType.NVarChar, 60).Value = priceText;
            command.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            command.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
            command.Parameters.Add("@doc", SqlDbType.NVarChar, 255).Value = contractName;
            command.Parameters.Add("@username", SqlDbType.NVarChar, 64).Value = Environment.UserName;
            var affected = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (affected != plan.TargetIds.Count)
                throw new DBConcurrencyException(
                    "Не все одинаковые позиции обновлены — изменения отменены.");
        }

        if (field == "price")
            SqlServerStages.RefreshLinkedPrices(conn, tx, contractId, plan.TargetIds);
        var next = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new PriceLikeResult(true, plan.TargetIds.Count, null, next);
    }
}
