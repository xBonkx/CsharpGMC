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
    public void UnknownCategoryAndNullFlags_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProcurementViewFilter.Matches("typo", "none", "", false, []));
        Assert.Throws<ArgumentNullException>(() =>
            ProcurementViewFilter.Matches("all", "none", "", false, null!));
    }
}
