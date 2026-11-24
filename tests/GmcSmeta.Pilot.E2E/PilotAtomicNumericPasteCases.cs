using System.Data;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

/// <summary>End-to-end atomic paste: only synthetic GmcSmetaPilot_* data.</summary>
internal static class PilotAtomicNumericPasteCases
{
    public static void Run(PilotRunner p)
    {
        var ids = new List<long>();
        using (var conn = p.Conn())
        using (var cmd = new SqlCommand(
            "SELECT TOP (2500) id FROM dbo.positions WHERE system_id=@system " +
            "ORDER BY [order],id", conn))
        {
            cmd.Parameters.Add("@system", SqlDbType.BigInt).Value = p.SmallSystem;
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        }
        PilotRunner.Check(ids.Count == 2500, "Synthetic batch fixture must contain 2500 positions");

        var head = p.Revision();
        var changes = ids.Select(id => new NumericPasteEdit(id, 777m)).ToArray();
        var saved = SqlServerDatabase.SaveNumericPasteAtomic(
            p.PilotConnection, p.SmallContract, "price", changes, head);
        PilotRunner.Check(saved.Saved && saved.Changed == 2500 &&
            saved.Requested == 2500 && saved.Revision == p.Revision() &&
            saved.Revision != head, "2500-row atomic COMMIT or meta.rev failed");

        using (var conn = p.Conn())
        {
            PilotRunner.Check(PilotRunner.Long(conn,
                "SELECT COUNT_BIG(*) FROM dbo.positions " +
                "WHERE system_id=@system AND price=777", ("@system", p.SmallSystem)) == 2500,
                "Not all 2500 prices persisted in the same COMMIT");
            PilotRunner.Check(PilotRunner.Double(conn,
                "SELECT price FROM dbo.act_lines WHERE position_id=@id " +
                "AND act_id IN (SELECT id FROM dbo.acts WHERE signed=1)",
                ("@id", p.FirstPosition)) == 100,
                "Signed KS-2 price changed during batch");
            PilotRunner.Check(PilotRunner.Double(conn,
                "SELECT price FROM dbo.act_lines WHERE position_id=@id " +
                "AND act_id IN (SELECT id FROM dbo.acts WHERE signed=0)",
                ("@id", p.FirstPosition)) == 777,
                "Unsigned KS-2 price was not synchronized");
        }

        var stale = SqlServerDatabase.SaveNumericPasteAtomic(
            p.PilotConnection, p.SmallContract, "price",
            [new NumericPasteEdit(ids[0], 123m)], head);
        PilotRunner.Check(!stale.Saved && p.Revision() == saved.Revision,
            "Stale revision was accepted after batch");

        var invalid = SqlServerDatabase.SaveNumericPasteAtomic(
            p.PilotConnection, p.SmallContract, "price",
            [new NumericPasteEdit(ids[0], 123m),
             new NumericPasteEdit(long.MaxValue, 123m)], saved.Revision);
        PilotRunner.Check(!invalid.Saved && p.Revision() == saved.Revision,
            "A missing row did not reject the entire batch");
        using (var conn = p.Conn())
            PilotRunner.Check(PilotRunner.Double(conn,
                "SELECT price FROM dbo.positions WHERE id=@id",
                ("@id", ids[0])) == 777,
                "Valid row was modified despite invalid batch target");

        // Same rollback guarantee for grouped cost edits, including the shared
        // single-cell writer's catalog/formula/act synchronization.
        var costBatch = SqlServerCostSummary.SaveSummaryCells(p.PilotConnection, (int)p.SmallContract,
            new[] { (ids[0], "888"), (long.MaxValue, "999") }, "price", saved.Revision);
        PilotRunner.Check(!costBatch.Saved && p.Revision() == saved.Revision,
            "Missing cost target did not reject the whole transaction");
        using (var conn = p.Conn())
            PilotRunner.Check(PilotRunner.Double(conn,
                "SELECT price FROM dbo.positions WHERE id=@id", ("@id", ids[0])) == 777,
                "Grouped cost edit partially committed");

        // Force a fault AFTER positions and unsigned act lines were updated.
        using (var conn = p.Conn())
            PilotRunner.Execute(conn,
                "CREATE TRIGGER dbo.pilot_fail_atomic_paste_log " +
                "ON dbo.change_log AFTER INSERT AS BEGIN " +
                "THROW 51042, 'synthetic atomic paste failure', 1; END");
        try
        {
            var threw = false;
            try
            {
                _ = SqlServerDatabase.SaveNumericPasteAtomic(
                    p.PilotConnection, p.SmallContract, "price",
                    [new NumericPasteEdit(ids[0], 888m),
                     new NumericPasteEdit(ids[1], 999m)], saved.Revision);
            }
            catch (SqlException) { threw = true; }
            PilotRunner.Check(threw, "Injected fault must abort atomic transaction");
        }
        finally
        {
            using var conn = p.Conn();
            PilotRunner.Execute(conn, "DROP TRIGGER IF EXISTS dbo.pilot_fail_atomic_paste_log");
        }
        using (var conn = p.Conn())
        {
            PilotRunner.Check(p.Revision() == saved.Revision,
                "Faulted transaction incorrectly bumped meta.rev");
            PilotRunner.Check(PilotRunner.Double(conn,
                "SELECT price FROM dbo.positions WHERE id=@id", ("@id", ids[0])) == 777 &&
                PilotRunner.Double(conn,
                "SELECT price FROM dbo.positions WHERE id=@id", ("@id", ids[1])) == 777,
                "Faulted transaction partially committed positions");
            PilotRunner.Check(PilotRunner.Double(conn,
                "SELECT price FROM dbo.act_lines WHERE position_id=@id " +
                "AND act_id IN (SELECT id FROM dbo.acts WHERE signed=0)",
                ("@id", p.FirstPosition)) == 777,
                "Faulted transaction partially committed act line");
        }
    }
}
