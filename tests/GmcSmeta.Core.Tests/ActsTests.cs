using System.Text.Json;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public class ActsTests
{
    private static DocRow Doc(int id, string kind, string number, int objectId = 1) =>
        new(id, objectId, kind, "", "", "", number, "", false, 0, 0, 0, 0);
    private static ActSummary Act(int id, int doc, string number, decimal total, bool signed = false) =>
        new(id, doc, number, new DateOnly(2026, 1, id), null, null, signed, total, total, total, 0, 0, 0, 0, 0, "");

    [Fact]
    public void ContractNumber_UsesExplicitBaseThenUniqueMainThenOwnNumber()
    {
        var main = Doc(1, "main", " 101 "); var dop = Doc(2, "dop", "3");
        Assert.Equal("101", Acts.ContractNumber(dop, new[] { main, dop }));
        var second = Doc(3, "main", "202");
        Assert.Equal("3", Acts.ContractNumber(dop, new[] { main, second, dop }));
        Assert.Equal("101", Acts.ContractNumber(dop with { BaseContractId = 1 }, new[] { main, second, dop }));
    }

    [Fact]
    public void Filters_PreserveFullGroupButFooterCountsVisibleActsOnly()
    {
        var docs = new[] { Doc(1, "main", "101"), Doc(2, "dop", "3"), Doc(3, "main", "101", 2) };
        var acts = new[] { Act(1, 1, "4.2", 20, true), Act(2, 2, "4.1", 30), Act(3, 3, "4.3", 999) };
        var all = Acts.List(1, "101", docs, acts);
        Assert.Equal(new[] { 0, 2, 1 }, all.Rows.Select(r => r.Id));
        var filtered = Acts.List(1, "101", docs, acts, "кс-3 подписан", signed: true);
        Assert.Equal(1, filtered.Count); Assert.Equal(20m, filtered.Total);
        Assert.Equal(50m, filtered.Rows[0].Total); Assert.Equal("1 из 2 КС-2", filtered.Rows[0].Source);
        Assert.Equal("На подготовке", filtered.Rows[0].Status);
        Assert.Equal(30m, Acts.List(1, "101", docs, acts, sourceId: 2).Total);
        Assert.Empty(Acts.List(1, "101", docs, acts, "несуществующий акт").Rows);
    }

    [Fact]
    public void GroupOrder_UsesEarliestActAndMissingDateSortsLastInPythonKey()
    {
        var docs = new[] { Doc(1, "main", "101") };
        var acts = new[] { Act(1, 1, "1.1", 1), Act(2, 1, "2.1", 1), Act(3, 1, "3.1", 1) with { Date = null } };
        Assert.Equal(new[] { "3", "2", "1" }, Acts.List(1, "101", docs, acts).Rows.Where(r => r.IsGroup).Select(r => r.GroupKey));
        Assert.Equal("4", Acts.GroupKey(Act(1, 1, "4-2", 1) with { GrossAdjustmentCents = 1 }));
        Assert.Equal("4-2", Acts.GroupKey(Act(1, 1, "4-2", 1)));
        Assert.Equal(2, Acts.Suffix("4-2")); Assert.Equal(1, Acts.Suffix("4.text"));
    }

    [Fact]
    public void ActBrowser_MatchesActualPythonRowsSourcesAndFilteredFooter()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "db", "base.db"))) root = root.Parent;
        if (root is null) return;
        using var conn = Database.Open(Path.Combine(root!.FullName, "db", "base.db"), readOnly: true);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "db", "acts_python.json")));
        var docs = Workspace.Documents(conn); var acts = FinanceReader.ReadActs(conn);
        var compared = 0;
        foreach (var expected in json.RootElement.EnumerateArray())
        {
            var obj = expected.GetProperty("object").GetInt32(); var number = expected.GetProperty("contract").GetString()!;
            var sources = Acts.Sources(obj, number, docs);
            var expectedSources = expected.GetProperty("sources").EnumerateArray().ToList();
            Assert.Equal(expectedSources.Count, sources.Count);
            for (var i = 0; i < sources.Count; i++)
            {
                Assert.Equal(expectedSources[i].GetProperty("caption").GetString(), sources[i].Caption);
                Assert.Equal(NullableInt(expectedSources[i].GetProperty("id")), sources[i].Id);
            }
            var status = expected.GetProperty("status").GetInt32();
            var result = Acts.List(obj, number, docs, acts, expected.GetProperty("query").GetString()!,
                NullableInt(expected.GetProperty("source")), status == 0 ? null : status == 2);
            var rows = expected.GetProperty("rows").EnumerateArray().ToList();
            Assert.Equal(rows.Count, result.Rows.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = result.Rows[i];
                Assert.Equal(rows[i].GetProperty("id").GetInt32(), row.Id);
                Assert.Equal(rows[i].GetProperty("group").GetBoolean(), row.IsGroup);
                Assert.Equal(rows[i].GetProperty("cells").EnumerateArray().Select(c => Normalize(c.GetString()!)),
                    new[] { row.Name, row.Source, row.DateText, row.PeriodText, Money.Format(row.Total), row.Status }.Select(Normalize));
            }
            Assert.Equal(Normalize(expected.GetProperty("footer").GetString()!),
                Normalize($"Показано актов: {result.Count}    ·    Сумма: {Money.Format(result.Total)} ₽"));
            compared++;
        }
        Assert.True(compared > 5);
    }
    private static int? NullableInt(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
    private static string Normalize(string text) => text.Replace('\u00a0', ' ').Replace('\u202f', ' ');
}
