using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>One numeric cell from an Excel column pasted into an existing estimate.</summary>
public sealed record NumericPasteEdit(long PositionId, decimal Value);

/// <summary>Saved=true means one complete, acknowledged SQL COMMIT, not queued work.</summary>
public sealed record NumericPasteResult(
    bool Saved, int Requested, int Changed, string Revision, string? Error)
{
    public static NumericPasteResult Fail(int requested, string revision, string error) =>
        new(false, requested, 0, revision, error);
}

public static partial class SqlServerDatabase
{
    public const int MaxAtomicNumericPasteRows = 5000;

    /// <summary>Pure preflight. SQL validation additionally checks contract, revision and locks.</summary>
    public static string? ValidateNumericPaste(
        long contractId, string field, IReadOnlyList<NumericPasteEdit>? edits)
    {
        if (contractId <= 0) return "Не выбран документ для вставки.";
        if (field is not ("qty" or "price" or "purchase_price" or "work_price"))
            return "Допустимы только количество, цена, закупка и цена работы.";
        if (edits is null || edits.Count == 0 || edits.Count > MaxAtomicNumericPasteRows)
            return $"За раз можно вставить от 1 до {MaxAtomicNumericPasteRows} чисел.";
        var ids = new HashSet<long>();
        foreach (var edit in edits)
        {
            if (edit.PositionId <= 0 || !ids.Add(edit.PositionId))
                return "Список вставки содержит неверные или повторяющиеся позиции.";
            if (edit.Value < 0 || edit.Value > NumberText.MaxNumber)
                return "Вставляемые числа должны быть от нуля до одного триллиона.";
        }
        return null;
    }

    /// <summary>
    /// All rows, formula cleanup, unsigned KS lines, m² summaries, history and ONE
    /// meta.rev update share one SERIALIZABLE SQL transaction. No blind retry.
    /// </summary>
    public static NumericPasteResult SaveNumericPasteAtomic(
        string connectionString, long contractId, string field,
        IReadOnlyList<NumericPasteEdit> edits, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        if (ValidateNumericPaste(contractId, field, edits) is { } invalid)
            return NumericPasteResult.Fail(edits?.Count ?? 0, expectedRevision, invalid);

        var values = edits.ToDictionary(e => e.PositionId, e => e.Value);
        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = LockRevision(conn, tx);
        if (revision is null)
            return NumericPasteResult.Fail(edits.Count, expectedRevision, "Отсутствует meta.rev.");
        if (!string.Equals(revision, expectedRevision, StringComparison.Ordinal))
            return NumericPasteResult.Fail(edits.Count, revision,
                "Смету изменили с другого компьютера. Обнови документ.");

        bool locked;
        string smrMode, title;
        long objectId;
        using (var doc = new SqlCommand(
            "SELECT locked,smr_mode,object_id,name FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE id=@id", conn, tx))
        {
            doc.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var reader = doc.ExecuteReader();
            if (!reader.Read())
                return NumericPasteResult.Fail(edits.Count, revision, "Документ удалён.");
            locked = !reader.IsDBNull(0) && Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture);
            smrMode = reader.IsDBNull(1) ? "" : reader.GetString(1);
            objectId = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
            title = reader.IsDBNull(3) ? "" : reader.GetString(3);
        }
        if (locked && field != "purchase_price")
            return NumericPasteResult.Fail(edits.Count, revision, "Подписанный документ нельзя менять.");
        if (field == "work_price" && smrMode != "pos")
            return NumericPasteResult.Fail(edits.Count, revision,
                "Цена работ разрешена только в режиме СМР «по позициям».");

        // A temporary table avoids SQL Server's 2100-parameter limit.
        using (var create = new SqlCommand(
            "CREATE TABLE #paste_ids (id BIGINT NOT NULL PRIMARY KEY)", conn, tx))
            create.ExecuteNonQuery();
        var idTable = new DataTable();
        idTable.Columns.Add("id", typeof(long));
        foreach (var edit in edits) idTable.Rows.Add(edit.PositionId);
        using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
            { DestinationTableName = "#paste_ids", BatchSize = 1000, BulkCopyTimeout = 120 })
        {
            bulk.ColumnMappings.Add("id", "id");
            bulk.WriteToServer(idTable);
        }

        // Validate every target BEFORE changing any user data.
        var changes = new DataTable();
        changes.Columns.Add("id", typeof(long));
        changes.Columns.Add("val", typeof(double));
        changes.Columns.Add("formulas", typeof(string));
        changes.Columns.Add("old_value", typeof(string));
        changes.Columns.Add("new_value", typeof(string));
        var touchedAreaChildren = new List<long>();
        var checkedCount = 0;
        using (var scan = new SqlCommand(
            "SELECT p.id,p.parent_id,p.formulas,p.qty,p.price,p.purchase_price,p.work_price " +
            "FROM #paste_ids AS ids JOIN dbo.positions AS p WITH (UPDLOCK,HOLDLOCK) ON p.id=ids.id " +
            "JOIN dbo.systems AS sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@contract ORDER BY p.id", conn, tx) { CommandTimeout = 120 })
        {
            scan.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            using var reader = scan.ExecuteReader();
            while (reader.Read())
            {
                checkedCount++;
                var id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                var isComponent = !reader.IsDBNull(1);
                if (field != "qty" && isComponent)
                    return NumericPasteResult.Fail(edits.Count, revision,
                        $"Позиция {id}: цены комплектующих отдельно не редактируются.");

                var oldJson = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var index = field switch
                {
                    "qty" => 3, "price" => 4, "purchase_price" => 5, _ => 6
                };
                decimal? previous = reader.IsDBNull(index) ? null :
                    Convert.ToDecimal(reader.GetValue(index), CultureInfo.InvariantCulture);
                JsonObject formulas;
                try
                {
                    formulas = string.IsNullOrWhiteSpace(oldJson) ? new JsonObject() :
                        JsonNode.Parse(oldJson) as JsonObject ??
                        throw new FormatException("Ожидается JSON-объект формул.");
                }
                catch (Exception ex) when (ex is JsonException or FormatException)
                {
                    return NumericPasteResult.Fail(edits.Count, revision,
                        $"Позиция {id}: нельзя безопасно обновить формулы: {ex.Message}");
                }
                // Excel contains numbers rather than formulas: remove only this field's formula.
                formulas.Remove(field);
                var nextJson = formulas.ToJsonString(FormulaOptions);
                var nextValue = values[id];
                if (previous == nextValue && JsonEquivalent(oldJson, nextJson)) continue;

                changes.Rows.Add(id, (double)nextValue, nextJson,
                    previous?.ToString(CultureInfo.InvariantCulture) ?? "",
                    nextValue.ToString(CultureInfo.InvariantCulture));
                if (field == "qty" && isComponent) touchedAreaChildren.Add(id);
            }
        }
        if (checkedCount != edits.Count)
            return NumericPasteResult.Fail(edits.Count, revision,
                "Часть позиций удалена или не принадлежит этому договору. Вставка отменена.");
        if (changes.Rows.Count == 0)
        {
            tx.Commit();
            return new NumericPasteResult(true, edits.Count, 0, revision, null);
        }

        using (var create = new SqlCommand(
            "CREATE TABLE #paste_changes (id BIGINT NOT NULL PRIMARY KEY, " +
            "val FLOAT NOT NULL, formulas NVARCHAR(MAX) NOT NULL, " +
            "old_value NVARCHAR(64) NOT NULL, new_value NVARCHAR(64) NOT NULL)", conn, tx))
            create.ExecuteNonQuery();
        using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
            { DestinationTableName = "#paste_changes", BatchSize = 1000, BulkCopyTimeout = 120 })
        {
            foreach (DataColumn column in changes.Columns)
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            bulk.WriteToServer(changes);
        }

        // Field name comes only from the four-element whitelist in ValidateNumericPaste.
        using (var update = new SqlCommand(
            $"UPDATE p SET [{field}]=s.val, formulas=s.formulas " +
            "FROM dbo.positions AS p JOIN #paste_changes AS s ON s.id=p.id", conn, tx)
            { CommandTimeout = 120 })
        {
            if (update.ExecuteNonQuery() != changes.Rows.Count)
                throw new DBConcurrencyException("Часть позиций исчезла при записи. Вся вставка отменена.");
        }

        if (field is "price" or "work_price")
        {
            using var updateActs = new SqlCommand(
                $"UPDATE al SET [{field}]=s.val FROM dbo.act_lines AS al " +
                "JOIN #paste_changes AS s ON s.id=al.position_id " +
                "JOIN dbo.acts AS a ON a.id=al.act_id " +
                "WHERE a.signed=0 AND a.contract_id=@contract", conn, tx)
                { CommandTimeout = 120 };
            updateActs.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            updateActs.ExecuteNonQuery();
        }

        // Apply area-summary recalculation after ALL child quantities were updated.
        if (field == "qty")
            foreach (var id in touchedAreaChildren)
                RefreshAreaParent(conn, tx, id, contractId, objectId, title, "");

        if (field == "work_price")
        {
            var acts = new List<long>();
            using (var readActs = new SqlCommand(
                "SELECT DISTINCT al.act_id FROM dbo.act_lines AS al " +
                "JOIN #paste_changes AS s ON s.id=al.position_id " +
                "JOIN dbo.acts AS a ON a.id=al.act_id " +
                "WHERE a.signed=0 AND a.contract_id=@contract", conn, tx))
            {
                readActs.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
                using var reader = readActs.ExecuteReader();
                while (reader.Read())
                    acts.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
            }
            SyncActWork(conn, tx, acts);
        }

        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "SELECT @ts,@user,N'position',p.id,N'update'," +
            "CONCAT(@title,N' / ',sy.name,N' / ',p.name,N': ',@field,N': «'," +
            "s.old_value,N'» → «',s.new_value,N'» (Excel)'),@contract,@object " +
            "FROM #paste_changes AS s " +
            "JOIN dbo.positions AS p ON p.id=s.id " +
            "JOIN dbo.systems AS sy ON sy.id=p.system_id", conn, tx)
            { CommandTimeout = 120 })
        {
            log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
            log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
            log.Parameters.Add("@title", SqlDbType.NVarChar, -1).Value = title;
            log.Parameters.Add("@field", SqlDbType.NVarChar, 32).Value = field;
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            log.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
            if (log.ExecuteNonQuery() != changes.Rows.Count)
                throw new DBConcurrencyException("История массовой вставки записана не полностью.");
        }

        var nextRevision = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new NumericPasteResult(true, edits.Count, changes.Rows.Count, nextRevision, null);
    }
}
