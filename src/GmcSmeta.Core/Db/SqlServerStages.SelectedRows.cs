using System.Data;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerStages
{
    /// <summary>
    /// Python stages_dialog.put_rest / take_out for an explicit selection.
    /// One revision, one transaction and no per-row public writer calls.
    /// </summary>
    public static (SqlServerDatabase.SaveResult Result, int Rows) ChangeSelectedStageRows(
        string connectionString, long fullId, long stageId,
        IReadOnlyCollection<long> sourceIds, bool putRest, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        var ids = sourceIds.ToHashSet();
        if (fullId <= 0 || stageId <= 0 || ids.Count == 0 ||
            ids.Any(id => id <= 0) || string.IsNullOrWhiteSpace(expectedRevision))
            return (SqlServerDatabase.SaveResult.Fail("Не выбран этап или позиции.", expectedRevision), 0);

        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null)
            return (SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision), 0);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return (SqlServerDatabase.SaveResult.Fail("Данные изменились. Обнови окно этапов.", rev), 0);
        if (StageWriteProblem(conn, tx, fullId, stageId) is { } problem)
            return (SqlServerDatabase.SaveResult.Fail(problem, rev), 0);

        var (_, rows) = StageTable(conn, fullId, tx);
        var selected = rows.Where(row => ids.Contains(row.SrcId)).ToArray();
        if (selected.Length != ids.Count)
            return (SqlServerDatabase.SaveResult.Fail("Не все выбранные позиции принадлежат полному КП.", rev), 0);

        var changed = 0;
        foreach (var row in selected)
        {
            if (putRest && row.Rest <= (decimal)Eps) continue;
            var mine = StageRowId(conn, tx, stageId, row.SrcId);
            if (!putRest && mine is null) continue;
            var qty = putRest
                ? (mine is long id ? CurrentQty(conn, tx, id) : 0m) + row.Rest
                : 0m;
            // Existing quantity and document dependency guards apply to each row.
            // An exception rolls back the entire selected batch.
            SetQtyCore(conn, tx, stageId, fullId, row, qty, mine);
            changed++;
        }

        if (changed == 0)
        {
            tx.Commit();
            return (new SqlServerDatabase.SaveResult(true, null, rev), 0);
        }

        RefreshMoney(conn, tx, fullId, stageId);
        var rest = RestDoc(conn, tx, fullId);
        if (rest is long restId && restId != stageId)
            SyncRestCore(conn, tx, fullId, restId);
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return (new SqlServerDatabase.SaveResult(true, null, updated), changed);
    }
}
