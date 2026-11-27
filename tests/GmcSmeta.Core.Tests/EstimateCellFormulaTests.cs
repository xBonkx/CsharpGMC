using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateCellFormulaTests
{
    [Fact]
    public void PickedNumbersBecomePersistableArithmeticNotOnlyResult()
    {
        var values = new Dictionary<int, decimal> { [1] = 12.5m, [2] = 3m };
        var expanded = EstimateCellFormula.Expand("=[1]*2-[2]", values);
        Assert.Equal("=(12.5)*2-(3)", expanded);
        Assert.Equal(22m, GmcSmeta.Core.NumberText.Parse(expanded));
        Assert.Equal(expanded, GmcSmeta.Core.NumberText.FormulaText(expanded));
        Assert.Equal(new[] { 1, 2 }, EstimateCellFormula.References("=[1]+[2]-[1]"));
        Assert.Null(EstimateCellFormula.Expand("=[1]+[3]", values));
        Assert.Null(EstimateCellFormula.Expand("=[1]/0", values));
    }

    [Fact]
    public void StageFormulaRejectsMismatchAndZeroBeforeOpeningDatabase()
    {
        var mismatch = GmcSmeta.Core.Db.SqlServerStages.SetStageQty("not a connection", 1, 2, 5m,
            "rev", qtyFormula: "=2+2");
        Assert.False(mismatch.Saved);
        var zero = GmcSmeta.Core.Db.SqlServerStages.SetStageQty("not a connection", 1, 2, 0m,
            "rev", qtyFormula: "=2-2");
        Assert.False(zero.Saved);
    }

    [Fact]
    public void StoredExpressionReopensAndOtherFieldsAreNotConfused()
    {
        Assert.Equal("=2*3", EstimateCellFormula.Read("{\"qty\":\"=2*3\",\"price\":\"=4+5\"}", "qty"));
        Assert.Equal("=4+5", EstimateCellFormula.Read("{\"qty\":\"=2*3\",\"price\":\"=4+5\"}", "price"));
        Assert.Equal("", EstimateCellFormula.Read("broken", "qty"));
        Assert.Equal("", EstimateCellFormula.Read("{}", "qty"));
    }
}
