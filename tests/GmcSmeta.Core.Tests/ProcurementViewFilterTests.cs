using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public class ProcurementViewFilterTests
{
    [Theory]
    [InlineData("none", "none", true)]
    [InlineData("none", "ordered", false)]
    [InlineData("work", "invoice", true)]
    [InlineData("work", "paid", true)]
    [InlineData("work", "shop_queued", true)]
    [InlineData("work", "shop_work", true)]
    [InlineData("work", "part_stock", false)]
    [InlineData("stock", "part_stock", true)]
    [InlineData("stock", "stock", true)]
    [InlineData("issued", "part_issued", true)]
    [InlineData("issued", "issued", true)]
    [InlineData("issued", "stock", false)]
    public void Categories_MatchPythonStatusSets(string category, string status, bool expected)
    {
        Assert.Equal(expected, ProcurementViewFilter.Matches(category, status,
            stage: "", isWork: false, flags: Array.Empty<string>()));
    }

    [Fact]
    public void All_DoesNotHideZeroOrUnorderedWorkRows()
    {
        Assert.True(ProcurementViewFilter.Matches("all", "zero", "", true, []));
        Assert.True(ProcurementViewFilter.Matches("all", "none", "", true, []));
    }

    [Fact]
    public void Late_UsesActualSQLServerWarningFlag_NotEveryDueDate()
    {
        Assert.True(ProcurementViewFilter.Matches("late", "ordered", "", false,
            ["просрочена поставка (ждали к 18.11)"]));
        Assert.False(ProcurementViewFilter.Matches("late", "ordered", "", false,
            ["замена"]));
    }

    [Fact]
    public void Shop_UsesSourceEvenAfterDerivedStatusChangesOnDelivery()
    {
        Assert.True(ProcurementViewFilter.Matches("shop", "stock", "", false, [], source: "shop"));
        Assert.True(ProcurementViewFilter.Matches("shop", "issued", "ordered", false, [], source: "shop"));
        Assert.False(ProcurementViewFilter.Matches("shop", "shop_queued", "shop_work",
            false, [], source: "buy"));
        Assert.False(ProcurementViewFilter.Matches("shop", "stock", "", false, [], source: "buy"));
    }

    [Fact]
    public void Problems_ExcludeSmrEvenIfFlagsExist()
    {
        Assert.True(ProcurementViewFilter.Matches("problem", "none", "", false, ["вне КП"]));
        Assert.False(ProcurementViewFilter.Matches("problem", "none", "", true, ["вне КП"]));
        Assert.False(ProcurementViewFilter.Matches("problem", "ordered", "", false, []));
    }

    [Fact]
    public void Counts_MatchIndividualFilters_AndEnumerateLargeSnapshotOnce()
    {
        string[] statuses = ["none", "skip", "zero", "fact", "ordered", "invoice", "paid",
            "shop_queued", "shop_work", "stock", "part_stock", "issued", "part_issued", "unknown"];
        var rows = Enumerable.Range(0, 25000).Select(i =>
            (Status: statuses[i % statuses.Length], IsWork: i % 7 == 0,
                Flags: (IReadOnlyList<string>)(i % 5 == 0 ? new[] { "ПРОСРОЧЕНА ПОСТАВКА", "замена" }
                    : i % 3 == 0 ? new[] { "вне КП" } : Array.Empty<string>()),
                Source: i % 4 == 0 ? "shop" : "buy")).ToArray();
        var enumerated = 0;
        IEnumerable<(string Status, bool IsWork, IReadOnlyList<string> Flags, string Source)> Once()
        {
            foreach (var row in rows)
            {
                enumerated++;
                yield return row;
            }
        }
        var counts = ProcurementViewFilter.CountCategories(Once());
        Assert.Equal(rows.Length, enumerated);
        int Expected(string category) => rows.Count(row =>
            ProcurementViewFilter.Matches(category, row.Status, "", row.IsWork, row.Flags, row.Source));
        Assert.Equal(Expected("all"), counts.All);
        Assert.Equal(Expected("none"), counts.None);
        Assert.Equal(Expected("work"), counts.Work);
        Assert.Equal(Expected("late"), counts.Late);
        Assert.Equal(Expected("shop"), counts.Shop);
        Assert.Equal(Expected("stock"), counts.Stock);
        Assert.Equal(Expected("issued"), counts.Issued);
        Assert.Equal(Expected("problem"), counts.Problem);
        Assert.Equal(default(ProcurementViewFilter.CategoryCounts),
            ProcurementViewFilter.CountCategories(rows.Take(0)));
    }

    [Fact]
    public void UnknownCategoryAndNullFlags_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProcurementViewFilter.Matches("typo", "none", "", false, []));
        Assert.Throws<ArgumentNullException>(() =>
            ProcurementViewFilter.Matches("all", "none", "", false, null!));
    }
}
