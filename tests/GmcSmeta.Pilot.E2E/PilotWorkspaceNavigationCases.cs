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
        PilotRunner.Check(!string.IsNullOrWhiteSpace(revision) &&
            SqlServerDatabase.Revision(conn) == revision,
            "Read-only navigation changed revision or left an active reader");
    }
}
