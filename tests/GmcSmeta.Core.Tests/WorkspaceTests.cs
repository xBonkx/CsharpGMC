using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public class WorkspaceTests
{
    private static DocRow Doc(int id, string kind, int order = 0) =>
        new(id, 1, kind, "", "", "", "", "", false, 0, 0, 0, 0) { Order = order };

    [Fact]
    public void KpTotals_CountOnlyNewestParentNotVariantsOrStages()
    {
        var docs = new[] { Doc(1, "kp", 1), Doc(2, "kp", 2), Doc(3, "kp", 99) with { StageOf = 2 }, Doc(4, "main") };
        Assert.Equal(new[] { 2 }, Workspace.CountedDocumentIds(docs, NavigationMode.Kp));
        Assert.Equal(new[] { 4 }, Workspace.CountedDocumentIds(docs, NavigationMode.Contracts));
    }

    [Fact]
    public void ArchiveMoney_CountsContractsAndDopsOrNewestProposalWithoutContracts()
    {
        var docs = new[] { Doc(1, "kp", 99), Doc(2, "main"), Doc(3, "dop") with { BaseContractId = 2 } };
        Assert.Equal(new[] { 2, 3 }, Workspace.CountedDocumentIds(docs, NavigationMode.Archive).Order());
        Assert.Equal(new[] { 2 }, Workspace.CountedDocumentIds(new[] { Doc(1, "kp"), Doc(2, "kp", 1) }, NavigationMode.Archive));
        Assert.Empty(Workspace.CountedDocumentIds(Array.Empty<DocRow>(), NavigationMode.Kp));
    }

    [Fact]
    public void SupplementaryCards_FollowTheirContractInOriginalSiblingOrder()
    {
        var docs = new[] { Doc(1, "main"), Doc(2, "main"), Doc(3, "dop") with { BaseContractId = 1 },
            Doc(4, "dop") with { BaseContractId = 1 }, Doc(5, "dop") with { BaseContractId = 999 } };
        Assert.Equal(new[] { 1, 3, 4, 2, 5 }, Workspace.OrderDocumentCards(docs).Select(d => d.Id));
    }

    [Fact]
    public void ObjectCardsAndTotals_MatchActualPythonPage()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "db", "base.db"))) root = root.Parent;
        if (root is null) return;
        var reference = Path.Combine(root!.FullName, "db", "objects_python.json");
        Assert.True(File.Exists(reference), "Generate Python reference: tools/preview_python.py --render");
        using var conn = Database.Open(Path.Combine(root.FullName, "db", "base.db"), readOnly: true);
        using var json = JsonDocument.Parse(File.ReadAllText(reference));
        var objects = Workspace.Objects(conn).ToDictionary(o => o.Id);
        var docs = Workspace.Documents(conn);
        var compared = 0;
        foreach (var snapshot in json.RootElement.EnumerateObject())
        {
            var parts = snapshot.Name.Split(':');
            var mode = parts[0] switch { "contracts" => NavigationMode.Contracts, "kp" => NavigationMode.Kp, _ => NavigationMode.Archive };
            var data = Workspace.ReadObjectDocuments(conn, objects[int.Parse(parts[1])], docs, mode);
            var expected = snapshot.Value;
            Assert.Equal(expected.GetProperty("progress_caption").GetString(), data.Progress.Caption);
            Assert.Equal(expected.GetProperty("show_progress").GetBoolean(), data.ShowProgress);
            Assert.Equal(Normalize(expected.GetProperty("earned").GetString()!), Normalize(data.EarnedText));
            Assert.Equal(expected.GetProperty("earned_caption").GetString(), data.EarnedCaption);
            Assert.Equal(expected.GetProperty("earned_notes").GetString(), data.EarnedNotes);
            Assert.Equal(expected.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("id").GetInt32()), data.Cards.Select(c => c.Id));
            foreach (var (field, value) in new[] { ("total", data.Total), ("materials", data.Materials), ("smr", data.Smr) })
                Assert.True(Math.Abs((double)value - expected.GetProperty(field).GetDouble()) < .006, snapshot.Name + ": " + field);
            var cards = expected.GetProperty("cards").EnumerateArray().ToList();
            for (var i = 0; i < cards.Count; i++)
            {
                var card = data.Cards[i];
                Assert.Equal(cards[i].GetProperty("title").GetString(), card.Title);
                Assert.Equal(cards[i].GetProperty("meta").GetString(), card.Meta);
                // Python money uses regular spaces; the Russian .NET locale uses NBSP.
                Assert.Equal(Normalize(cards[i].GetProperty("split").GetString()!), Normalize(card.SplitText));
                Assert.Equal(cards[i].GetProperty("base").GetString(), card.BaseTitle.Length > 0 ? "к договору: " + card.BaseTitle : "");
            }
            compared += cards.Count;
        }
        Assert.Equal(docs.Count, compared);
    }

    private static string Normalize(string text) => text.Replace('\u00A0', ' ').Replace('\u202F', ' ');
}
