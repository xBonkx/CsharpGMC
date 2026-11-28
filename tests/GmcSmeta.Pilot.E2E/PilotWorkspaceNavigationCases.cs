using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

internal static class PilotWorkspaceNavigationCases
{
    internal static void Run(PilotRunner p)
    {
        // Explicitly disable MARS; the new reader must finish both result sets.
        var cs = new SqlConnectionStringBuilder(p.PilotConnection)
        { MultipleActiveResultSets = false }.ConnectionString;
        using var conn = SqlServerDatabase.Open(cs);
        p.RequireOwned(conn);
        var revision = SqlServerDatabase.Revision(conn);
        var expectedObjects = Workspace.Objects(conn);
        var expectedDocuments = Workspace.Documents(conn);
        var actual = Workspace.ReadNavigation(conn);
        var consistent = Workspace.ReadConsistentNavigation(conn);
        PilotRunner.Check(consistent.Objects.SequenceEqual(actual.Objects) &&
            consistent.Documents.SequenceEqual(actual.Documents),
            "Revision-fenced refresh changed navigation data");
        PilotRunner.Check(expectedObjects.SequenceEqual(actual.Objects) &&
            expectedDocuments.SequenceEqual(actual.Documents),
            "Navigation batch changed order, metadata, counts or monetary totals");
        PilotRunner.Check(expectedDocuments.Any(d => d.Positions >= 10000),
            "Large-document fixture missing from navigation comparison");

        foreach (var obj in actual.Objects)
        {
            PilotRunner.Check(Workspace.ObjectCardMetadataMatches(conn, obj, actual.Documents),
                "Scoped metadata rejected an unchanged object");
        }
        var first = actual.Objects.First(o => actual.Documents.Any(d => d.ObjectId == o.Id));
        PilotRunner.Check(!Workspace.ObjectCardMetadataMatches(conn,
            first with { Name = first.Name + " stale" }, actual.Documents),
            "Scoped metadata accepted stale object name");
        var firstDoc = actual.Documents.First(d => d.ObjectId == first.Id);
        var stale = actual.Documents.Select(d => d.Id == firstDoc.Id
            ? d with { Name = d.Name + " stale" } : d).ToArray();
        PilotRunner.Check(!Workspace.ObjectCardMetadataMatches(conn, first, stale),
            "Scoped metadata accepted stale document");
        PilotRunner.Check(!Workspace.ObjectCardMetadataMatches(conn, first,
            actual.Documents.Where(d => d.Id != firstDoc.Id).ToArray()),
            "Scoped metadata accepted missing document");
        PilotRunner.Check(!Workspace.ObjectCardMetadataMatches(conn,
            first with { Id = int.MaxValue }, actual.Documents),
            "Missing object must not match an empty metadata batch");
        using var cancellation = new CancellationTokenSource();
        var large = actual.Documents.First(d => d.Positions >= 10000);
        var oldUndo = SqlServerUndo.Snapshot(conn, null, large.Id);
        var newUndo = SqlServerUndo.ReadConsistent(cs, large.Id, revision, cancellation.Token);
        PilotRunner.Check(oldUndo is not null && oldUndo.Equals(newUndo),
            "Revision-fenced Undo baseline changed a large snapshot");
        PilotRunner.Check(SqlServerUndo.ReadConsistent(cs, large.Id, "intentionally-stale-pilot-revision") is null,
            "Undo baseline accepted an obsolete revision");
        PilotRunner.Check(Workspace.ReadDocumentMetadata(conn, large.Id) == Workspace.ReadDocumentMetadata(conn, large.Id, cancellation.Token),
            "Cancellable document metadata changed data");
        PilotRunner.Check(SqlServerSpecUpdate.LatestRevision(conn, null, large.Id) == SqlServerSpecUpdate.LatestRevision(conn, null, large.Id, cancellation.Token),
            "Cancellable spec head changed data");
        cancellation.Cancel();
        try
        {
            SqlServerUndo.Snapshot(conn, null, large.Id, cancellation.Token);
            throw new InvalidOperationException("Pre-cancelled Undo snapshot did not stop");
        }
        catch (OperationCanceledException) { }
        PilotRunner.Check(!string.IsNullOrWhiteSpace(revision) &&
            SqlServerDatabase.Revision(conn) == revision,
            "Read-only navigation changed revision or left an active reader");
    }
}
