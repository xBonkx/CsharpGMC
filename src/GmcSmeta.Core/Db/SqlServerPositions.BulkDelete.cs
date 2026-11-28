using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerDatabase
{
    /// <summary>
    /// Atomically deletes all selected positions (across sections) and the components of
    /// selected roots. A single protected row rejects the entire batch; never partially delete.
    /// Position IDs are always verified against the contract under the revision/SQL locks.
    /// </summary>
    public static SaveResult DeletePositions(string connectionString, long contractId,
        IReadOnlyCollection<long> positionIds, string expectedRevision)
    {
        if (contractId <= 0 || positionIds is null || positionIds.Count == 0 ||
            positionIds.Any(id => id <= 0))
            return SaveResult.Fail("Выбери позиции для удаления.", expectedRevision);

        var selectedIds = positionIds.Distinct().Order().ToArray();
        if (selectedIds.Length > 10_000)
            return SaveResult.Fail("За один раз можно удалить не более 10 000 позиций.", expectedRevision);

        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = LockRevision(conn, tx);
        if (revision is null)
            return SaveResult.Fail("Нет meta.rev.", expectedRevision);
        if (!string.Equals(revision, expectedRevision, StringComparison.Ordinal))
            return SaveResult.Fail("Документ изменён другим сотрудником. Обнови смету.", revision);

        var contract = LoadPositionContract(conn, tx, contractId);
        if (contract is null)
            return SaveResult.Fail("Документ не найден.", revision);
        if (contract.Value.Locked)
            return SaveResult.Fail("Подписанную смету менять нельзя.", revision);

        // Temporary ID table is filled in chunks to stay below SQL Server's 2100-parameter limit.
        CreateIdTable(conn, tx, "#selected_delete_positions", selectedIds);
        using (var verify = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.systems s WITH (UPDLOCK,HOLDLOCK) ON s.id=p.system_id " +
            "JOIN #selected_delete_positions d ON d.id=p.id WHERE s.contract_id=@contract", conn, tx))
        {
            verify.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (Convert.ToInt64(verify.ExecuteScalar(), CultureInfo.InvariantCulture) != selectedIds.Length)
                return SaveResult.Fail("Некоторые выделенные позиции отсутствуют в этом договоре. Обнови смету.", revision);
        }

        // Include components of selected roots. If a component itself is selected, remove
        // only that component; its unselected parent remains untouched.
        // A parameterized SqlCommand executes in sp_executesql scope. A local
        // temp table created inside that batch disappears when it returns.
        // Create it on the connection first, then populate it with parameters.
        using (var create = new SqlCommand(
            "CREATE TABLE #delete_positions (id BIGINT NOT NULL PRIMARY KEY, " +
            "system_id BIGINT NOT NULL, parent_id BIGINT NULL, name NVARCHAR(MAX) NULL)", conn, tx))
            create.ExecuteNonQuery();
        using (var build = new SqlCommand(
            "INSERT INTO #delete_positions (id,system_id,parent_id,name) " +
            "SELECT p.id,p.system_id,p.parent_id,p.name " +
            "FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.systems s WITH (UPDLOCK,HOLDLOCK) ON s.id=p.system_id " +
            "WHERE s.contract_id=@contract AND (" +
            "EXISTS (SELECT 1 FROM #selected_delete_positions d WHERE d.id=p.id) OR " +
            "EXISTS (SELECT 1 FROM #selected_delete_positions d WHERE d.id=p.parent_id))", conn, tx))
        {
            build.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            build.ExecuteNonQuery();
        }

        var requested = new List<(long Id, string Name)>();
        using (var cmd = new SqlCommand(
            "SELECT p.id,p.name FROM #delete_positions p " +
            "JOIN #selected_delete_positions d ON d.id=p.id ORDER BY p.id", conn, tx))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                requested.Add((
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    reader.IsDBNull(1) ? "" : reader.GetString(1)));
        if (requested.Count != selectedIds.Length)
            throw new DBConcurrencyException("Состав выделенных позиций изменился.");

        var affectedSections = new List<long>();
        using (var cmd = new SqlCommand(
            "SELECT DISTINCT system_id FROM #delete_positions ORDER BY system_id", conn, tx))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                affectedSections.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
        foreach (var section in affectedSections)
            TreeOrder(LoadOrderedPositions(conn, tx, contractId, section));

        // The entire deletion is rejected even for a draft/unsigned KS-2 reference.
        using (var check = new SqlCommand(
            "SELECT TOP(1) p.name,a.number FROM dbo.act_lines al WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN #delete_positions d ON d.id=al.position_id " +
            "JOIN dbo.positions p ON p.id=al.position_id JOIN dbo.acts a ON a.id=al.act_id", conn, tx))
        using (var reader = check.ExecuteReader())
        {
            if (reader.Read())
            {
                var name = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var number = reader.IsDBNull(1) ? "" :
                    Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "";
                return SaveResult.Fail(
                    $"Удаление всей группы отменено: «{name[..Math.Min(60, name.Length)]}» есть в КС-2 № {number}.",
                    revision);
            }
        }
        using (var check = new SqlCommand(
            "SELECT TOP(1) m.id FROM dbo.movements m WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN #delete_positions d ON d.id=m.position_id WHERE m.shipment_id IS NOT NULL", conn, tx))
            if (check.ExecuteScalar() is not null)
                return SaveResult.Fail("Удаление всей группы отменено: есть проведённая выдача.", revision);
        using (var check = new SqlCommand(
            "SELECT TOP(1) sl.id FROM dbo.shipment_lines sl WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN #delete_positions d ON d.id=sl.position_id " +
            "JOIN dbo.shipments sh ON sh.id=sl.shipment_id WHERE sh.fixed=1", conn, tx))
            if (check.ExecuteScalar() is not null)
                return SaveResult.Fail("Удаление всей группы отменено: есть закреплённая ведомость выдачи.", revision);

        foreach (var table in new[] { "procurement", "movements", "shipment_lines" })
        {
            using var delete = new SqlCommand(
                $"DELETE t FROM dbo.[{table}] t JOIN #delete_positions d ON d.id=t.position_id",
                conn, tx);
            delete.ExecuteNonQuery();
        }

        long doomedCount;
        using (var count = new SqlCommand("SELECT COUNT_BIG(*) FROM #delete_positions", conn, tx))
            doomedCount = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);

        using var deleteChildren = new SqlCommand(
            "DELETE p FROM dbo.positions p JOIN #delete_positions d ON d.id=p.id " +
            "WHERE p.parent_id IS NOT NULL", conn, tx);
        var childrenDeleted = deleteChildren.ExecuteNonQuery();
        using var deleteRoots = new SqlCommand(
            "DELETE p FROM dbo.positions p JOIN #delete_positions d ON d.id=p.id " +
            "WHERE p.parent_id IS NULL", conn, tx);
        var rootsDeleted = deleteRoots.ExecuteNonQuery();
        if (childrenDeleted + rootsDeleted != doomedCount)
            throw new DBConcurrencyException("Состав строк изменился при удалении.");

        // One renumbering pass for every affected section, rather than one query per row.
        using (var renumber = new SqlCommand(
            "WITH ord AS (SELECT p.id,ROW_NUMBER() OVER " +
            "(PARTITION BY p.system_id ORDER BY p.[order],p.id)-1 AS n " +
            "FROM dbo.positions p JOIN (SELECT DISTINCT system_id FROM #delete_positions) s " +
            "ON s.system_id=p.system_id) " +
            "UPDATE p SET [order]=CONVERT(INT,ord.n) " +
            "FROM dbo.positions p JOIN ord ON ord.id=p.id WHERE p.[order]<>ord.n", conn, tx))
            renumber.ExecuteNonQuery();

        foreach (var row in requested)
            LogPositionChange(conn, tx, row.Id, contractId, contract.Value.ObjectId, "delete",
                $"{contract.Value.DocumentName} / {row.Name[..Math.Min(80, row.Name.Length)]} (пакетное удаление)");
        var updatedRevision = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SaveResult(true, null, updatedRevision);
    }
}
