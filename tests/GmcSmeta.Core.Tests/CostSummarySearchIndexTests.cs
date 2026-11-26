using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class CostSummarySearchIndexTests
{
    private static CostSummaryRow Row(int i) =>
        new(i / 2, i % 4 + 1, i % 3 == 0 ? "Клапан ДУ" : "Воздуховод ЁЖ", "шт", 2m,
            i % 2 == 0 ? "Поставщик Север" : "Юг",
            10.01m, 20.02m, 30.03m, 40.04m,
            (i & 2) != 0, (i & 1) != 0, (i & 8) != 0, (i & 4) != 0,
            null, 0m, null, null, false, null, false, false);

    [Theory]
    [InlineData("")]
    [InlineData("  ВОЗДУХОВОД   север ")]
    [InlineData("группа п1")]
    [InlineData("юг клапан")]
    [InlineData("ёж")]
    [InlineData("нет совпадений")]
    [InlineData("клапан\tду")]
    public void FilterPreservesOrderRowsAndAllMissingCombinations(string query)
    {
        var rows = Enumerable.Range(1, 25000).Select(Row).ToArray();
        var labels = new Dictionary<long, string> { [1] = "Группа › П1", [2] = "П2", [3] = "Группа › П3" };
        var index = new CostSummarySearchIndex(rows, labels);
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant()).ToArray();
        IReadOnlySet<long>?[] selections = { null, new HashSet<long> { 1, 3 }, new HashSet<long>() };
        foreach (var systems in selections)
        foreach (var missingOnly in new[] { false, true })
        for (var mask = 0; mask < 16; mask++)
        {
            bool Original(CostSummaryRow row) =>
                (systems is null || systems.Contains(row.SystemId)) &&
                (!missingOnly || ((mask & 1) != 0 && row.NoPrice) ||
                    ((mask & 2) != 0 && row.NoCost) || ((mask & 4) != 0 && row.NowSell) ||
                    ((mask & 8) != 0 && row.NowCost)) &&
                words.All(w => $"{row.Name} {labels.GetValueOrDefault(row.SystemId, "")} {row.Supplier}"
                    .ToLowerInvariant().Contains(w));
            var expected = rows.Where(Original).ToArray();
            var actual = index.Filter(query, systems, missingOnly, (CostMissingPrice)mask).ToArray();
            Assert.Equal(expected.Length, actual.Length);
            for (var i = 0; i < expected.Length; i++) Assert.Same(expected[i], actual[i]);
        }
    }

    [Fact]
    public void ReplacementSnapshotUsesNewNamesSuppliersAndLabels()
    {
        var old = Row(1);
        var updated = old with { Name = "Новая позиция", Supplier = "Другой" };
        var before = new CostSummarySearchIndex(new[] { old },
            new Dictionary<long, string> { [old.SystemId] = "Старая система" });
        var after = new CostSummarySearchIndex(new[] { updated },
            new Dictionary<long, string> { [updated.SystemId] = "Новая система" });
        Assert.Single(before.Filter("старая система", null, false, CostMissingPrice.None));
        Assert.Empty(after.Filter("старая система", null, false, CostMissingPrice.None));
        Assert.Same(updated, Assert.Single(after.Filter("новая другой", null, false, CostMissingPrice.None)));
    }
}
