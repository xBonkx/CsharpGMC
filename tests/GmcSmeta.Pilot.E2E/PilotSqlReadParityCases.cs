using GmcSmeta.Core.Db;
using GmcSmeta.RemoteSqlBench;
using Microsoft.Data.SqlClient;

internal static class PilotSqlReadParityCases
{
    internal static void ProcurementLarge(PilotRunner p)
    {
        using var conn = p.Conn();
        p.RequireOwned(conn);
        foreach (var id in new[] { p.SmallContract, p.LargeContract })
        {
            var parity = SqlDiagnosticQueries.CompareBuyRows(conn, checked((int)id));
            PilotRunner.Check(parity.Passed, "Large procurement correlated row parity failed");
            PilotRunner.Check(parity.ExpectedRows >= 2500, "Large fixture has insufficient rows");
        }
    }

    // Simulate the legacy client contract: only the shared meta.rev changes.
    // Do not assume document-specific revisions exist, even for unrelated edits.
    internal static void LegacyRevision(PilotRunner p)
    {
        using var conn = p.Conn();
        p.RequireOwned(conn);
        var id = checked((int)p.SmallContract);
        var before = SqlServerProcurement.ReadBuySnapshot(p.PilotConnection, id);
        var director = SqlServerDirector.ReadIfChanged(p.PilotConnection, "kp", "", null)
            ?? throw new InvalidOperationException("No director snapshot");
        PilotRunner.Check(before.Revision == director.Revision, "Fixture revision changed before legacy test");
        var oldNote = PilotRunner.Text(conn, "SELECT note FROM dbo.positions WHERE id=@id", ("@id", p.FirstPosition));
        var next = "legacy-pilot-" + Guid.NewGuid().ToString("N");
        PilotRunner.Execute(conn, "UPDATE dbo.meta SET [value]=@value WHERE [key]=N'rev'", ("@value", next));
        var stale = SqlServerDatabase.SaveCell(p.PilotConnection, p.FirstPosition, "note", "must-not-write", before.Revision);
        PilotRunner.Check(!stale.Saved && stale.Revision == next &&
            PilotRunner.Text(conn, "SELECT note FROM dbo.positions WHERE id=@id", ("@id", p.FirstPosition)) == oldNote,
            "Legacy global revision allowed a stale write");
        var freshBuy = SqlServerProcurement.ReadBuySnapshotIfChanged(p.PilotConnection, id, before.Revision, before.Day);
        var freshDirector = SqlServerDirector.ReadIfChanged(p.PilotConnection, "kp", director.Revision, director.Day);
        PilotRunner.Check(freshBuy?.Revision == next && freshDirector?.Revision == next,
            "Legacy revision did not invalidate both cached readers");
        PilotRunner.Check(SqlServerProcurement.ReadBuySnapshotIfChanged(p.PilotConnection, id, next, DateTime.Today) is null &&
            SqlServerDirector.ReadIfChanged(p.PilotConnection, "kp", next, DateTime.Today) is null,
            "Unchanged revision should reuse confirmed snapshots");
    }

    // The runner owns a disposable nonce-marked fixture. Only the schema
    // VERSION marker is fault-injected, never DDL or an external database.
    internal static void SchemaRevalidation(PilotRunner p)
    {
        using var control = p.Conn();
        p.RequireOwned(control);
        var version = PilotRunner.Text(control, "SELECT [value] FROM dbo.meta WHERE [key]=N'schema_version'");
        var revision = SqlServerDatabase.Revision(control);
        PilotRunner.Check(version == "28", "Expected synthetic schema 28");
        var pooled = new SqlConnectionStringBuilder(p.PilotConnection) { Pooling = true }.ConnectionString;
        var fresh = new SqlConnectionStringBuilder(p.PilotConnection) { Pooling = false }.ConnectionString;

        void Accept(string cs, bool asyncOpen)
        {
            using var connection = asyncOpen
                ? SqlServerDatabase.OpenAsync(cs, CancellationToken.None).GetAwaiter().GetResult()
                : SqlServerDatabase.Open(cs);
            p.RequireOwned(connection);
            PilotRunner.Check(PilotRunner.Long(connection,
                "SELECT CONVERT(bigint,SESSIONPROPERTY('QUOTED_IDENTIFIER'))") == 1, "Session guard missing");
        }
        void Reject(string cs, bool asyncOpen)
        {
            try
            {
                using var connection = asyncOpen
                    ? SqlServerDatabase.OpenAsync(cs, CancellationToken.None).GetAwaiter().GetResult()
                    : SqlServerDatabase.Open(cs);
            }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Invalid schema was accepted by guarded open");
        }
        Accept(pooled, false);
        Accept(pooled, true);
        try
        {
            PilotRunner.Execute(control, "UPDATE dbo.meta SET [value]=N'999' WHERE [key]=N'schema_version'");
            Reject(pooled, false);
            Reject(pooled, true);
            Reject(fresh, false);
            Reject(fresh, true);
        }
        finally
        {
            PilotRunner.Execute(control, "UPDATE dbo.meta SET [value]=@version WHERE [key]=N'schema_version'",
                ("@version", version));
        }
        Accept(pooled, false);
        Accept(fresh, true);
        PilotRunner.Check(SqlServerDatabase.Revision(control) == revision, "Guarded read changed business revision");
    }
}
