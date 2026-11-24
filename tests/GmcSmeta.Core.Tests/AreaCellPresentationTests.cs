using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class AreaCellPresentationTests
{
    [Fact]
    public void DuctPerimeterTimesQuantity_MatchesPython()
    {
        var c = AreaCellPresentation.For("Воздуховод 200x100", "м", 5m, null, false);
        Assert.Equal("0,6", c.Unit);
        Assert.Equal("3", c.Total);
        Assert.False(c.Warning);
        Assert.False(c.Manual);
        Assert.Contains("периметр", c.UnitTip);
    }

    [Fact]
    public void ManuallyEnteredAreaOverridesAutomaticGeometry()
    {
        var c = AreaCellPresentation.For("Воздуховод 200x100", "м", 5m, 0.25, false);
        Assert.Equal("0,25", c.Unit);
        Assert.Equal("1,25", c.Total);
        Assert.True(c.Manual);
        Assert.Contains("вписано вручную", c.UnitTip);
    }

    [Fact]
    public void SummaryRowShowsOnlySummedQuantityAsArea()
    {
        var c = AreaCellPresentation.For("Воздуховоды м²", "м²", 12.345m, null, true);
        Assert.Equal("", c.Unit);
        Assert.Equal("", c.UnitEditor);
        Assert.Equal("12,345", c.Total);
    }

    [Fact]
    public void UnparseableDuctShowsWarningButOrdinaryEquipmentDoesNot()
    {
        var bad = AreaCellPresentation.For("Воздуховод без размеров", "м", 2m, null, false);
        Assert.True(bad.Warning);
        Assert.Equal("", bad.Unit);
        var ordinary = AreaCellPresentation.For("Вентилятор", "шт", 3m, null, false);
        Assert.False(ordinary.Warning);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("0", true)]
    [InlineData("=2*0,35", true)]
    [InlineData("12,5", true)]
    [InlineData("-1", false)]
    [InlineData("abc", false)]
    [InlineData("3/0", false)]
    public void AreaManualValidation(string input, bool accepted)
    {
        Assert.Equal(accepted, AreaCellPresentation.TryParseManual(input, out var parsed));
        if (input == "") Assert.Null(parsed);
        if (input == "=2*0,35") Assert.Equal(0.7m, parsed);
    }

    [Fact]
    public void WorkCodeHeaderFollowsDocumentForm()
    {
        Assert.Equal("Код работ", AreaCellPresentation.BasisHeader("r4"));
        Assert.Equal("Обоснование", AreaCellPresentation.BasisHeader("dns"));
        Assert.True(AreaCellPresentation.IsBasisAllowed("r4", false));
        Assert.False(AreaCellPresentation.IsBasisAllowed("general", false));
        Assert.True(AreaCellPresentation.IsBasisAllowed("general", true));
    }

    [Fact]
    public void TotalIsRoundedToTwoDecimalsOnlyForDisplay()
    {
        var c = AreaCellPresentation.For("Воздуховод 200x100", "м", 1.005m, null, false);
        Assert.Equal("0,6", c.Total);
        Assert.Contains("0,603 м²", c.TotalTip);
    }
    [Fact]
    public void SummaryQuantitySumsUnroundedChildrenBeforeRounding()
    {
        var rows = new (string Name, string Unit, double Qty, double? Manual)[]
        {
            ("Воздуховод 200x100", "м", 1, null),
            ("Воздуховод 200x100", "м", 1, 0.1575),
            ("нерассчитываемая позиция", "шт", 1, null)
        };
        Assert.Equal(0.758m, AreaCellPresentation.SummaryQuantity(rows));
    }

    [Fact]
    public void WorkCodeFromR4PrefersBasisOverImportColumnB()
    {
        Assert.Equal("ГЭСН-12", AreaCellPresentation.WorkCode("ГЭСН-12", "{\"B\":\"старое\"}"));
        Assert.Equal("ФЕР-10", AreaCellPresentation.WorkCode("", "{\"B\":\"ФЕР-10\"}"));
        Assert.Equal("", AreaCellPresentation.WorkCode("", "not-json"));
    }

}
