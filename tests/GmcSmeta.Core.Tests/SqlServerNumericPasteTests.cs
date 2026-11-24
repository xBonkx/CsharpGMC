using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class SqlServerNumericPasteTests
{
    [Theory]
    [InlineData("qty")]
    [InlineData("price")]
    [InlineData("purchase_price")]
    [InlineData("work_price")]
    public void NumericColumns_AcceptLargeValidInputWithoutSql(string field)
    {
        var edits = Enumerable.Range(1, 2500)
            .Select(id => new NumericPasteEdit(id, id / 10000m)).ToArray();
        Assert.Null(SqlServerDatabase.ValidateNumericPaste(1, field, edits));
    }

    [Fact]
    public void NumericBatch_RejectsBadIdsDuplicatesAndInvalidValues()
    {
        static string? Check(params NumericPasteEdit[] edits) =>
            SqlServerDatabase.ValidateNumericPaste(1, "price", edits);

        Assert.NotNull(Check());
        Assert.NotNull(Check(new NumericPasteEdit(0, 1m)));
        Assert.NotNull(Check(new NumericPasteEdit(1, 1m), new NumericPasteEdit(1, 2m)));
        Assert.NotNull(Check(new NumericPasteEdit(1, -1m)));
        Assert.NotNull(Check(new NumericPasteEdit(1, NumberText.MaxNumber + 1m)));
        Assert.NotNull(SqlServerDatabase.ValidateNumericPaste(0, "price",
            [new NumericPasteEdit(1, 1m)]));
        Assert.NotNull(SqlServerDatabase.ValidateNumericPaste(1, "note",
            [new NumericPasteEdit(1, 1m)]));
        Assert.NotNull(SqlServerDatabase.ValidateNumericPaste(1, "qty",
            Enumerable.Range(1, SqlServerDatabase.MaxAtomicNumericPasteRows + 1)
                .Select(id => new NumericPasteEdit(id, 1m)).ToArray()));
    }

    [Fact]
    public void ProductionConnection_CannotReachBatchSql()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerDatabase.SaveNumericPasteAtomic(
                "Server=.;Database=GmcSmetaProduction;Trusted_Connection=True",
                1, "price", [new NumericPasteEdit(1, 12m)], "rev"));
    }
}
