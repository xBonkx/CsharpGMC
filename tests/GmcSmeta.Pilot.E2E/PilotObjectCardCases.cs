using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

internal static class PilotObjectCardCases
{
    internal static void Run(PilotRunner p)
    {
        using var conn = SqlServerDatabase.Open(new SqlConnectionStringBuilder(p.PilotConnection)
            { MultipleActiveResultSets = false }.ConnectionString);
        p.RequireOwned(conn);
        var revision = SqlServerDatabase.Revision(conn);
        var (objects, documents) = Workspace.ReadNavigation(conn);
        var ids = documents.Select(d => d.Id).ToArray();
        var batch = EstimateReader.LoadCardsSql(conn, ids.Concat(ids));
        PilotRunner.Check(batch.Count == ids.Length &&
            EstimateReader.LoadCardsSql(conn, Array.Empty<int>()).Count == 0,
            "Duplicate or empty card scope changed");
        foreach (var id in ids)
        {
            var full = EstimateReader.Load(conn, id);
            PilotRunner.Check(batch[id] == EstimateCardData.FromEstimate(full),
                $"Lightweight card differs from full estimate {id}");
        }
        PilotRunner.Check(batch.Values.Any(d => d.Positions >= 10000),
            "Large-document fixture missing from card comparison");

        foreach (var obj in objects)
        foreach (var mode in Enum.GetValues<NavigationMode>())
        {
            var selected = documents.Where(d => d.ObjectId == obj.Id &&
                (mode == NavigationMode.Archive || (mode == NavigationMode.Kp
                    ? d.Kind == "kp" : d.Kind is "main" or "dop"))).ToList();
            var page = Workspace.ReadObjectDocuments(conn, obj, documents, mode);
            PilotRunner.Check(page.Cards.Select(c => c.Id).SequenceEqual(
                Workspace.OrderDocumentCards(selected).Select(d => d.Id)),
                "Card order or mode filter changed");
            foreach (var card in page.Cards)
                PilotRunner.Check(card.Estimate == batch[card.Id],
                    "Workspace did not publish the matching card projection");
            var counted = Workspace.CountedDocumentIds(
                documents.Where(d => d.ObjectId == obj.Id).ToList(), mode);
            var expected = selected.Where(d => counted.Contains(d.Id)).Select(d => batch[d.Id]).ToList();
            PilotRunner.Check(page.Total == expected.Sum(d => d.Totals.Total) &&
                page.Materials == expected.Sum(d => d.Statistics.Materials) &&
                page.Smr == expected.Sum(d => d.Statistics.Smr) &&
                page.VatTop == expected.Where(d => d.VatTop).Sum(d => d.Totals.Vat),
                "Object financial totals changed");
        }

        PilotRunner.Check(!string.IsNullOrWhiteSpace(revision) &&
            SqlServerDatabase.Revision(conn) == revision,
            "Card projection changed revision or retained a reader");
    }
}
