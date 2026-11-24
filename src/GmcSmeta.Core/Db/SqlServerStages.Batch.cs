using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Atomic stage-mode bulk operations. The UI may preview the batch, but
/// the server rebuilds and compares the entire selected snapshot under one
/// SERIALIZABLE transaction before any row is changed.
/// </summary>
public static partial class SqlServerStages
{
    public sealed record StageBatchResult(bool Saved, int Positions, int Changes,
        string? Error, string Revision)
    {
        public static StageBatchResult Fail(string error, string revision) =>
            new(false, 0, 0, error, revision);
    }

    public static StageBatchResult ChangeSelected(string connectionString,
        long fullId, long? targetStageId, StageBatchPlanner.Action action,
        IReadOnlyCollection<long> positionIds, IReadOnlyList<long> expectedStageIds,
        IReadOnlyCollection<StageBatchPlanner.Snapshot> expectedRows,
        string expectedRevision)
    {
        if (fullId <= 0 || positionIds is null || expectedRows is null ||
            expectedStageIds is null || positionIds.Count == 0 ||
            positionIds.Count > StageBatchPlanner.MaxPositions ||
            positionIds.Any(x => x <= 0) ||
            positionIds.Distinct().Count() != positionIds.Count ||
            expectedStageIds.Count > 1000 ||
            expectedStageIds.Distinct().Count() != expectedStageIds.Count ||
            expectedStageIds.Any(x => x <= 0) ||
            expectedRows.Count != positionIds.Count)
            return StageBatchResult.Fail("Недопустимое выделение или список этапов.",
                expectedRevision);

        if (action is not (StageBatchPlanner.Action.PutRemaining or
                            StageBatchPlanner.Action.ReturnSelected))
            return StageBatchResult.Fail("Недопустимая массовая операция.", expectedRevision);
        if (action == StageBatchPlanner.Action.PutRemaining &&
            (targetStageId is not long target || !expectedStageIds.Contains(target)))
            return StageBatchResult.Fail("Выбранного этапа нет в загруженном КП.",
                expectedRevision);
        if (action == StageBatchPlanner.Action.ReturnSelected && targetStageId is not null)
            return StageBatchResult.Fail("Для возврата строк не выбирают конкретный этап.",
                expectedRevision);

        // This new multi-row write is intentionally pilot-gated pending live
        // acceptance. No production SQL connection is even opened here.
        string database;
        try { database = new SqlConnectionStringBuilder(connectionString).InitialCatalog; }
        catch (ArgumentException)
        {
            return StageBatchResult.Fail("Некорректная строка SQL Server.", expectedRevision);
        }
        if (!database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            return StageBatchResult.Fail(
                "Массовое распределение пока разрешено только в тестовой базе GmcSmetaPilot_*.",
                expectedRevision);

        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null)
            return StageBatchResult.Fail("Не найдена ревизия meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return StageBatchResult.Fail(
                "КП изменилось в другом окне. Обнови смету и повтори.", rev);

        // Lock and validate the whole source document, not just its visible
        // row IDs. No stage allocations from another full KP are permitted.
        using (var check = new SqlCommand(
            "SELECT stage_of,kind FROM dbo.contracts WITH (HOLDLOCK) WHERE id=@full",
            conn, tx))
        {
            check.Parameters.Add("@full", SqlDbType.BigInt).Value = fullId;
            using var r = check.ExecuteReader();
            if (!r.Read() || !r.IsDBNull(0) || r.IsDBNull(1) ||
                !string.Equals(r.GetString(1), "kp", StringComparison.OrdinalIgnoreCase))
                return StageBatchResult.Fail("Это не полное КП. Распределение отменено.", rev);
        }

        var (stages, table) = StageTable(conn, fullId, tx);
        if (!stages.Select(x => x.Id).SequenceEqual(expectedStageIds))
            return StageBatchResult.Fail("Состав или порядок этапов изменился. Обнови смету.", rev);

        var snapshots = table.Select(row => new StageBatchPlanner.Snapshot(
            row.SrcId, row.Qty, row.Rest, row.PerStage)).ToArray();
        if (!StageBatchPlanner.Matches(expectedRows, snapshots, positionIds, stages.Count))
            return StageBatchResult.Fail(
                "Количество в одной из выделенных строк изменилось. Обнови смету.", rev);

        var targetIndex = action == StageBatchPlanner.Action.PutRemaining
            ? stages.FindIndex(stage => stage.Id == targetStageId) : -1;
        if (action == StageBatchPlanner.Action.PutRemaining && targetIndex < 0)
            return StageBatchResult.Fail("Выбранный этап больше не существует.", rev);

        var changes = StageBatchPlanner.Plan(snapshots, positionIds, stages.Count,
            action, targetIndex);
        if (changes.Count == 0)
        {
            tx.Commit();
            return new StageBatchResult(true, 0, 0, null, rev);
        }

        var touchedStages = changes.Select(x => x.StageIndex).Distinct().ToArray();
        foreach (var index in touchedStages)
            if (StageWriteProblem(conn, tx, fullId, stages[index].Id) is { } problem)
                return StageBatchResult.Fail(problem + " Массовая операция отменена целиком.", rev);

        var byId = table.ToDictionary(x => x.SrcId);
        var objectId = ObjectOfContract(conn, tx, fullId);
        using var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,N'position',@id,N'edit',@details,@contract,@object)", conn, tx);
        log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
        log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
        log.Parameters.Add("@id", SqlDbType.BigInt);
        log.Parameters.Add("@details", SqlDbType.NVarChar, -1);
        log.Parameters.Add("@contract", SqlDbType.BigInt).Value = fullId;
        log.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;

        foreach (var change in changes)
        {
            var stage = stages[change.StageIndex];
            var row = byId[change.PositionId];
            var rowId = StageRowId(conn, tx, stage.Id, change.PositionId);
            SetQtyCore(conn, tx, stage.Id, fullId, row, change.After, rowId);
            log.Parameters["@id"].Value = change.PositionId;
            log.Parameters["@details"].Value =
                $"Массовое распределение: {stage.Name} / " +
                $"{row.Name[..Math.Min(row.Name.Length, 70)]}: " +
                $"{change.Before.ToString("0.######", CultureInfo.InvariantCulture)} → " +
                $"{change.After.ToString("0.######", CultureInfo.InvariantCulture)}";
            log.ExecuteNonQuery();
        }

        foreach (var index in touchedStages)
            RefreshMoney(conn, tx, fullId, stages[index].Id);

        // The computed rest, its components and monetary totals are refreshed
        // only ONCE, in the SAME transaction as all allocations.
        var restId = RestDoc(conn, tx, fullId);
        if (restId is long rest)
            SyncRestCore(conn, tx, fullId, rest);

        var nextRevision = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new StageBatchResult(true,
            changes.Select(x => x.PositionId).Distinct().Count(), changes.Count,
            null, nextRevision);
    }
}
