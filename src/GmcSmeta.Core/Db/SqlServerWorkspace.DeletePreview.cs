using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerWorkspace
{
    public sealed record ContractDeleteInfo(string Title, string ObjectName, int Acts, int Movements,
        IReadOnlyList<string> Details);

    public sealed record ObjectDeleteInfo(string Name, IReadOnlyList<string> Details);

    /// <summary>Что удаляется вместе с документом — текст подтверждений (kp_editor.delete_contract).</summary>
    public static ContractDeleteInfo DescribeContractForDelete(SqlConnection conn, SqlTransaction? tx, long id)
    {
        string title, objectName;
        using (var cmd = new SqlCommand(
            "SELECT c.kind,c.number,c.[date],c.name,o.name FROM dbo.contracts c " +
            "JOIN dbo.objects o ON o.id=c.object_id WHERE c.id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Документ не найден.");
            string Text(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            title = Documents.ContractTitleText(Text(0), Text(1), Text(2), Text(3));
            objectName = Text(4);
        }
        int acts;
        using (var cmd = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.acts WHERE contract_id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            acts = checked((int)SqlInteger.RequireInt64(cmd.ExecuteScalar()));
        }
        int movements;
        using (var cmd = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.movements m JOIN dbo.positions p ON p.id=m.position_id " +
            "JOIN dbo.systems s ON s.id=p.system_id WHERE s.contract_id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            movements = checked((int)SqlInteger.RequireInt64(cmd.ExecuteScalar()));
        }
        var details = WorkspaceDeletePresentation.ContractDetails(acts, movements);
        return new ContractDeleteInfo(title, objectName, acts, movements, details);
    }

    /// <summary>Что удаляется вместе с объектом — текст первого подтверждения (main_window.delete_object).</summary>
    public static ObjectDeleteInfo DescribeObjectForDelete(SqlConnection conn, SqlTransaction? tx, long id)
    {
        string name;
        using (var cmd = new SqlCommand("SELECT name FROM dbo.objects WHERE id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            var value = cmd.ExecuteScalar();
            if (value is null or DBNull) throw new ArgumentException("Объект не найден.");
            name = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        var details = new List<string>();
        var titles = new List<string>();
        using (var cmd = new SqlCommand(
            "SELECT kind,number,[date],name FROM dbo.contracts WHERE object_id=@id ORDER BY [order],id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                titles.Add(Documents.ContractTitleText(
                    r.IsDBNull(0) ? "kp" : r.GetString(0),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? "" : r.GetString(3)));
        }
        if (WorkspaceDeletePresentation.DocumentDetails(titles) is { } documentDetails)
            details.Add(documentDetails);
        using (var cmd = new SqlCommand(
            "SELECT COUNT_BIG(*), COALESCE(SUM(CASE WHEN signed=1 THEN 1 ELSE 0 END), 0) FROM dbo.acts " +
            "WHERE contract_id IN (SELECT id FROM dbo.contracts WHERE object_id=@id)", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            using var r = cmd.ExecuteReader();
            // SUM по int возвращает int: читаем через Convert, не GetInt64.
            if (r.Read() && Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture) > 0)
            {
                var acts = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                var signed = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
                details.Add(WorkspaceDeletePresentation.ActDetails(acts, signed));
            }
        }
        using (var cmd = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.shipments WHERE object_id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            var count = SqlInteger.RequireInt64(cmd.ExecuteScalar());
            if (count > 0) details.Add("ведомости выдачи: " + count);
        }
        using (var cmd = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.movements WHERE position_id IN (" +
            "SELECT id FROM dbo.positions WHERE system_id IN (" +
            "SELECT id FROM dbo.systems WHERE contract_id IN " +
            "(SELECT id FROM dbo.contracts WHERE object_id=@id)))", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            var count = SqlInteger.RequireInt64(cmd.ExecuteScalar());
            if (count > 0) details.Add($"история закупок и выдачи: {count} записей");
        }
        return new(name, details);
    }


    public sealed record ObjectDeletePreview(ObjectDeleteInfo Info, IReadOnlyList<long> DocumentIds);

    // Read-only preflight uses exactly the same snapshot policy as the writer.
    // The short transaction is disposed without any INSERT/UPDATE/DELETE.
    public static ObjectDeletePreview PrepareObjectDelete(string connection, long id, string revision)
    {
        RequirePilotDatabase(connection);
        using var conn = SqlServerDatabase.Open(connection);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        CheckRevision(conn, tx, revision);
        SqlServerTrash.ValidateObjectDeletion(conn, tx, id);
        return new(DescribeObjectForDelete(conn, tx, id), ReadDeletionDocumentIds(conn, tx, id));
    }

    public static ContractDeleteInfo PrepareContractDelete(string connection, long id, long objectId, string revision)
    {
        RequirePilotDatabase(connection);
        using var conn = SqlServerDatabase.Open(connection);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        CheckRevision(conn, tx, revision);
        SqlServerTrash.ValidateContractDeletion(conn, tx, id, objectId);
        return DescribeContractForDelete(conn, tx, id);
    }

    internal static long[] ReadDeletionDocumentIds(SqlConnection conn, SqlTransaction tx, long objectId)
    {
        using var cmd = new SqlCommand("SELECT id FROM dbo.contracts WHERE object_id=@id ORDER BY id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
        return ids.ToArray();
    }

    internal static void RequireDeletionScope(SqlConnection conn, SqlTransaction tx, long id,
        bool wholeObject, long? expectedObjectId = null)
    {
        var root = wholeObject
            ? "SELECT COUNT_BIG(*) FROM dbo.objects WHERE id=@id AND archived=0"
            : "SELECT COUNT_BIG(*) FROM dbo.contracts c JOIN dbo.objects o ON o.id=c.object_id " +
              "WHERE c.id=@id AND o.archived=0 AND c.locked=0 AND c.stage_of IS NULL AND ISNULL(c.stage_rest,0)=0 " +
              "AND (@object IS NULL OR c.object_id=@object)";
        using (var cmd = new SqlCommand(root, conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            if (!wholeObject) cmd.Parameters.Add("@object", SqlDbType.BigInt).Value = (object?)expectedObjectId ?? DBNull.Value;
            if (SqlInteger.RequireInt64(cmd.ExecuteScalar()) != 1)
                throw new InvalidOperationException("Карточка архивная, документ заблокирован, является этапом или не принадлежит выбранной карточке.");
        }

        // These inbound references are not part of a standalone trash snapshot.
        // Reject them even if a future SQL FK uses SET NULL or CASCADE.
        var contracts = wholeObject ? "SELECT id FROM dbo.contracts WHERE object_id=@id" : "SELECT id FROM dbo.contracts WHERE id=@id";
        var systems = $"SELECT id FROM dbo.systems WHERE contract_id IN ({contracts})";
        var positions = $"SELECT id FROM dbo.positions WHERE system_id IN ({systems})";
        var checks = new[]
        {
            $"SELECT 1 FROM dbo.contracts WHERE id NOT IN ({contracts}) AND (stage_of IN ({contracts}) OR base_contract_id IN ({contracts}))",
            $"SELECT 1 FROM dbo.systems WHERE id NOT IN ({systems}) AND (parent_id IN ({systems}) OR stage_sys_src IN ({systems}))",
            $"SELECT 1 FROM dbo.positions WHERE id NOT IN ({positions}) AND (parent_id IN ({positions}) OR stage_src IN ({positions}))",
            $"SELECT 1 FROM dbo.act_lines al JOIN dbo.acts a ON a.id=al.act_id WHERE a.contract_id NOT IN ({contracts}) AND al.position_id IN ({positions})"
        };
        foreach (var check in checks)
        {
            using var cmd = new SqlCommand("SELECT CASE WHEN EXISTS(" + check + ") THEN 1 ELSE 0 END", conn, tx);
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            if (checked((int)SqlInteger.RequireInt64(cmd.ExecuteScalar())) != 0)
                throw new InvalidOperationException("Есть ссылки из других документов. Удаление без потери связей невозможно.");
        }
    }

    private static void RequireSingleDeletion(SqlConnection conn, SqlTransaction tx, string table, long id)
    {
        if (table is not ("objects" or "contracts")) throw new ArgumentException(nameof(table));
        using var cmd = new SqlCommand($"DELETE dbo.[{table}] WHERE id=@id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        if (cmd.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Удаляемая запись изменилась.");
    }

    private static void CommitDeletion(SqlTransaction tx)
    {
        try { tx.Commit(); }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
        {
            throw new TrashCommitUnknownException(ex);
        }
    }
}
