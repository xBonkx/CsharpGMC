using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Additional installation costs and per-position work costs, from Python smr_view.
/// The source estimate's work_price is intentionally not modified here.
/// </summary>
public static class SqlServerSmrExpenses
{
    public sealed record Expense(long Id, DateTime? Date, string Kind, string Who,
        decimal Amount, string Note);

    public static IReadOnlyList<Expense> List(string connectionString, long contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        return List(conn, contractId);
    }

    public static IReadOnlyList<Expense> List(SqlConnection conn, long contractId) =>
        List(conn, contractId, CancellationToken.None);

    public static IReadOnlyList<Expense> List(SqlConnection conn, long contractId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var cmd = new SqlCommand(
            "SELECT id,[date],kind,who,amount,note FROM dbo.smr_costs " +
            "WHERE contract_id=@id ORDER BY id", conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var cancellation = SqlReadCancellation.Register(cmd, token);
        using var reader = SqlReadCancellation.ExecuteReader(cmd, token);
        var result = new List<Expense>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            result.Add(new Expense(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3),
                reader.IsDBNull(4) ? 0m : Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? "" : reader.GetString(5)));
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static SqlConnection OpenPilot(string cs)
    {
        SqlServerWorkspace.RequirePilotDatabase(cs);
        return SqlServerDatabase.Open(cs);
    }

    private static string Revision(SqlConnection conn, SqlTransaction tx, string expected)
    {
        var rev = SqlServerDatabase.LockRevision(conn, tx)
            ?? throw new InvalidOperationException("Отсутствует meta.rev.");
        if (rev != expected)
            throw new DBConcurrencyException("Данные изменены другим пользователем. Обновите СМР.");
        return rev;
    }

    private static void Log(SqlConnection conn, SqlTransaction tx, long contractId,
        long rowId, string action, string details)
    {
        using var log = new SqlCommand(
            "INSERT dbo.change_log(ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES(SYSDATETIME(),@u,N'smr_cost',@row,@a,@d,@c, " +
            "(SELECT object_id FROM dbo.contracts WHERE id=@c))", conn, tx);
        log.Parameters.Add("@u", SqlDbType.NVarChar, 64).Value = Environment.UserName;
        log.Parameters.Add("@row", SqlDbType.BigInt).Value = rowId;
        log.Parameters.Add("@a", SqlDbType.NVarChar, 24).Value = action;
        log.Parameters.Add("@d", SqlDbType.NVarChar, -1).Value = details;
        log.Parameters.Add("@c", SqlDbType.BigInt).Value = contractId;
        log.ExecuteNonQuery();
    }

    public static (long Id, string Revision) Add(string connectionString, long contractId,
        string expectedRevision)
    {
        using var conn = OpenPilot(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        Revision(conn, tx, expectedRevision);
        long id;
        using (var add = new SqlCommand(
            "INSERT dbo.smr_costs(contract_id,[date],kind,who,amount,note,created_at) " +
            "OUTPUT inserted.id VALUES(@c,CONVERT(date,SYSDATETIME()),N'Аренда / техника',N'',0,N'',SYSDATETIME())",
            conn, tx))
        {
            add.Parameters.Add("@c", SqlDbType.BigInt).Value = contractId;
            id = Convert.ToInt64(add.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        Log(conn, tx, contractId, id, "create", "Добавлена затрата на монтаж");
        var next = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return (id, next);
    }

    public static SqlServerDatabase.SaveResult Save(string connectionString, long contractId, long rowId,
        string field, string input, string expectedRevision)
    {
        if (field is not ("date" or "kind" or "who" or "amount" or "note"))
            return SqlServerDatabase.SaveResult.Fail("Недопустимая колонка затрат СМР.", expectedRevision);
        object value;
        var trimmed = input.Trim();
        if (field == "date")
        {
            if (trimmed.Length == 0) value = DBNull.Value;
            else if (DateTime.TryParseExact(trimmed, new[] { "dd.MM.yyyy", "dd.MM.yy" },
                CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out var date))
                value = date.Date;
            else return SqlServerDatabase.SaveResult.Fail("Дата должна быть в формате дд.мм.гггг.", expectedRevision);
        }
        else if (field == "amount")
        {
            var n = trimmed.Length == 0 ? 0m : GmcSmeta.Core.NumberText.Parse(trimmed);
            if (n is null || n < 0m)
                return SqlServerDatabase.SaveResult.Fail("Недопустимая сумма затрат.", expectedRevision);
            value = (double)n.Value;
        }
        else
        {
            var limit = field == "kind" ? 64 : field == "who" ? 255 : int.MaxValue;
            if (trimmed.Length > limit)
                return SqlServerDatabase.SaveResult.Fail("Текст превышает длину поля.", expectedRevision);
            value = trimmed;
        }
        using var conn = OpenPilot(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = Revision(conn, tx, expectedRevision);
        using (var update = new SqlCommand(
            "UPDATE dbo.smr_costs SET [" + field + "]=@value WHERE id=@id AND contract_id=@contract",
            conn, tx))
        {
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = rowId;
            update.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            update.Parameters.Add("@value", field switch
            {
                "date" => SqlDbType.Date,
                "amount" => SqlDbType.Float,
                _ => SqlDbType.NVarChar,
            }, field switch { "kind" => 64, "who" => 255, _ => -1 }).Value = value;
            if (update.ExecuteNonQuery() != 1)
                return SqlServerDatabase.SaveResult.Fail("Затрата не найдена в этом договоре.", rev);
        }
        Log(conn, tx, contractId, rowId, "update", field + ": " + trimmed);
        var next = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, next);
    }

    public static SqlServerDatabase.SaveResult Delete(string connectionString, long contractId,
        IReadOnlyList<long> rowIds, string expectedRevision)
    {
        var ids = rowIds.Where(x => x > 0).Distinct().ToArray();
        if (ids.Length == 0)
            return SqlServerDatabase.SaveResult.Fail("Не выбраны затраты СМР.", expectedRevision);
        using var conn = OpenPilot(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = Revision(conn, tx, expectedRevision);
        foreach (var chunk in ids.Chunk(400))
        {
            using var cmd = new SqlCommand { Connection = conn, Transaction = tx };
            var paramNames = new List<string>();
            for (var i = 0; i < chunk.Length; i++)
            {
                var param = "@p" + i.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.Add(param, SqlDbType.BigInt).Value = chunk[i];
                paramNames.Add(param);
            }
            cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            cmd.CommandText = "DELETE FROM dbo.smr_costs WHERE contract_id=@contract AND id IN (" +
                string.Join(",", paramNames) + ")";
            if (cmd.ExecuteNonQuery() != chunk.Length)
                throw new DBConcurrencyException("Не все затраты принадлежат договору — удаление отменено.");
        }
        foreach (var id in ids)
            Log(conn, tx, contractId, id, "delete", "Затрата на монтаж удалена");
        var next = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, next);
    }

    public static (int Changed, string Revision) SetWorkUnitPrices(string connectionString, int contractId,
        IReadOnlyList<long> rowIds, string field, decimal? value, bool onlyEmpty, string expectedRevision)
    {
        if (field is not ("work_cost" or "work_sell"))
            throw new ArgumentException("Разрешены только внутренние цены монтажа.", nameof(field));
        GmcSmeta.Core.Domain.WorkPriceBatchPolicy.ValidateSelection(contractId, rowIds, value ?? 0m);
        var ids = rowIds.ToArray();
        using var conn = OpenPilot(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = Revision(conn, tx, expectedRevision);
        var changed = 0;
        foreach (var chunk in ids.Chunk(400))
        {
            using var cmd = new SqlCommand { Connection = conn, Transaction = tx };
            var names = new List<string>();
            for (var i = 0; i < chunk.Length; i++)
            {
                var name = "@p" + i.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.Add(name, SqlDbType.BigInt).Value = chunk[i];
                names.Add(name);
            }
            cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            cmd.Parameters.Add("@v", SqlDbType.Float).Value =
                value is null ? DBNull.Value : (object)(double)value.Value;
            var scope = " FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
                "JOIN dbo.systems sy ON sy.id=p.system_id " +
                "JOIN dbo.contracts c ON c.id=sy.contract_id JOIN dbo.objects o ON o.id=c.object_id " +
                "WHERE sy.contract_id=@contract AND o.archived=0 AND p.parent_id IS NULL " +
                "AND ISNULL(p.out_of_kp,0)=0 AND p.id IN (" + string.Join(",", names) + ")";
            cmd.CommandText = "SELECT COUNT_BIG(*)" + scope;
            if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != chunk.Length)
                throw new DBConcurrencyException("Выделение содержит чужие, отсутствующие или архивные позиции. Изменение отменено.");
            // Internal work costs and selling prices remain editable according
            // to the existing cost-accounting rules, including signed estimates.
            cmd.CommandText = "UPDATE p SET [" + field + "]=@v" + scope +
                (onlyEmpty ? " AND p.[" + field + "] IS NULL" : "") +
                " AND ((@v IS NULL AND p.[" + field + "] IS NOT NULL) OR " +
                "(@v IS NOT NULL AND (p.[" + field + "]<>@v OR p.[" + field + "] IS NULL)))";
            changed += cmd.ExecuteNonQuery();
        }
        if (changed > 0)
        {
            Log(conn, tx, contractId, contractId, "update",
                "Массовые цены СМР: " + field + ", строк " + changed);
            rev = SqlServerDatabase.BumpPositionRevision(conn, tx);
        }
        tx.Commit();
        return (changed, rev);
    }
}
