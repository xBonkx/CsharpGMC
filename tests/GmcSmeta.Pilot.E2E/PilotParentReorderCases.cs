using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using SaveResult = GmcSmeta.Core.Db.SqlServerDatabase.SaveResult;

internal static class PilotParentReorderCases
{
    internal static void Run(PilotRunner p)
    {
        var cs = p.PilotConnection;
        using var conn = p.Conn();
        p.RequireOwned(conn);
        long Section(string name, long? parent = null)
        {
            var r = SqlServerDatabase.AddSection(cs, p.SmallContract, name,
                parent.HasValue, parent, p.Revision());
            PilotRunner.Check(r.Saved && r.SystemId.HasValue, r.Error ?? "section");
            return r.SystemId!.Value;
        }
        long Position(long? parent = null)
        {
            var r = SqlServerDatabase.AddPosition(cs, p.SmallContract, p.SmallSystem,
                null, parent, p.Revision());
            PilotRunner.Check(r.Saved && r.PositionId.HasValue, r.Error ?? "position");
            return r.PositionId!.Value;
        }
        var other = Section("Other");
        var sub1 = Section("Sub1", p.SmallSystem);
        var sub2 = Section("Sub2", p.SmallSystem);
        var second = Position();
        var component = Position(second);
        var third = Position();
        PilotRunner.Check(SqlServerDatabase.SaveCell(cs, second, "qty", "4", p.Revision()).Saved,
            "quantity fixture");
        var stage = SqlServerStages.CreateStage(cs, p.SmallContract, p.Revision());
        PilotRunner.Check(SqlServerStages.SetStageQty(cs, stage, second, 2, p.Revision()).Saved,
            "allocation fixture");
        PilotRunner.Check(SqlServerWorkspace.SetContractLocked(cs, stage, true, p.Revision()),
            "signed stage fixture");
        string Snapshot() => PilotRunner.Text(conn, @"
            SELECT
              (SELECT c.* FROM dbo.contracts c ORDER BY c.id FOR JSON PATH, INCLUDE_NULL_VALUES) contracts,
              (SELECT s.* FROM dbo.systems s WHERE s.contract_id<>@doc
                ORDER BY s.id FOR JSON PATH, INCLUDE_NULL_VALUES) child_sections,
              (SELECT p.* FROM dbo.positions p JOIN dbo.systems s ON s.id=p.system_id
                WHERE s.contract_id<>@doc ORDER BY p.id FOR JSON PATH, INCLUDE_NULL_VALUES) child_positions,
              (SELECT id,contract_id,parent_id,name,smr_amount,heading,source_num,stage_sys_src
                FROM dbo.systems WHERE contract_id=@doc ORDER BY id
                FOR JSON PATH, INCLUDE_NULL_VALUES) parent_sections,
              (SELECT p.id,p.system_id,p.parent_id,p.qty,p.price,p.stage_src
                FROM dbo.positions p JOIN dbo.systems s ON s.id=p.system_id
                WHERE s.contract_id=@doc ORDER BY p.id FOR JSON PATH, INCLUDE_NULL_VALUES) parent_positions
            FOR JSON PATH, INCLUDE_NULL_VALUES", ("@doc", p.SmallContract));
        var before = Snapshot();
        void Saved(SaveResult r) => PilotRunner.Check(r.Saved, r.Error ?? "reorder failed");
        string PositionOrder() => PilotRunner.Text(conn,
            "SELECT STRING_AGG(CONVERT(nvarchar(max),id),',') WITHIN GROUP(ORDER BY [order],id) " +
            "FROM dbo.positions WHERE system_id=@s", ("@s", p.SmallSystem));
        Saved(SqlServerDatabase.DragPositions(cs, p.SmallContract, new[] { second },
            p.SmallSystem, null, p.Revision()));
        PilotRunner.Check(PositionOrder() == $"{second},{component},{p.FirstPosition},{third}",
            "mouse reorder must keep components");
        Saved(SqlServerDatabase.MovePosition(cs, p.SmallContract, p.SmallSystem, second, 1, p.Revision()));
        PilotRunner.Check(PositionOrder() == $"{p.FirstPosition},{second},{component},{third}", "move down");
        Saved(SqlServerDatabase.MovePosition(cs, p.SmallContract, p.SmallSystem, second, -1, p.Revision()));
        PilotRunner.Check(PositionOrder().StartsWith($"{second},{component},"), "move up");
        Saved(SqlServerDatabase.DragSystem(cs, p.SmallContract, other, null, p.Revision()));
        PilotRunner.Check(PilotRunner.Long(conn,
            "SELECT TOP(1) id FROM dbo.systems WHERE contract_id=@doc ORDER BY [order],id",
            ("@doc", p.SmallContract)) == other, "root section must move first");
        Saved(SqlServerDatabase.DragSubsection(cs, p.SmallContract, sub2, p.SmallSystem, null, p.Revision()));
        PilotRunner.Check(PilotRunner.Long(conn,
            "SELECT TOP(1) id FROM dbo.systems WHERE parent_id=@id ORDER BY [order],id",
            ("@id", p.SmallSystem)) == sub2, "subsection must move first");
        PilotRunner.Check(Snapshot() == before, "identities, hierarchy, quantities and signed stage changed");
        void Rejected(SaveResult r)
        {
            PilotRunner.Check(!r.Saved, "unsafe mutation accepted");
            PilotRunner.Check(Snapshot() == before, "rejected operation mutated data");
        }
        string Orders() => PilotRunner.Text(conn, @"SELECT
            (SELECT id,[order] FROM dbo.systems ORDER BY id FOR JSON PATH) sections,
            (SELECT id,[order] FROM dbo.positions ORDER BY id FOR JSON PATH) positions
            FOR JSON PATH");
        var orderBeforeReject = Orders();
        var revisionBeforeReject = p.Revision();
        Rejected(SqlServerDatabase.DragPositions(cs, p.SmallContract, new[] { second }, other, null, p.Revision()));
        Rejected(SqlServerDatabase.DragSubsection(cs, p.SmallContract, sub1, other, null, p.Revision()));
        Rejected(SqlServerDatabase.DragSystem(cs, p.SmallContract, other, p.SmallSystem, "stale"));
        Rejected(SqlServerDatabase.DragSystem(cs, stage, other, null, p.Revision()));
        PilotRunner.Check(Orders() == orderBeforeReject && p.Revision() == revisionBeforeReject,
            "rejected operations changed order/revision");
        PilotRunner.Execute(conn, "UPDATE dbo.contracts SET locked=1 WHERE id=@id", ("@id", p.SmallContract));
        var signed = SqlServerDatabase.DragSystem(cs, p.SmallContract, other, p.SmallSystem, p.Revision());
        PilotRunner.Check(!signed.Saved, "signed parent reordered");
        PilotRunner.Execute(conn, "UPDATE dbo.contracts SET locked=0 WHERE id=@id", ("@id", p.SmallContract));
        PilotRunner.Check(Snapshot() == before, "signed parent guard mutated data");
        // Use fresh readers as well as SQL order assertions.
        var estimate = EstimateReader.Load(conn, checked((int)p.SmallContract));
        PilotRunner.Check(estimate.PositionsInOrder.Any(r => r.Id == second), "reader lost source row");
    }
}
