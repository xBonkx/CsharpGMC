using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class ProcurementExtraValidationTests
{
    [Theory]
    [InlineData("Воздуховод", "шт", 1.0)]
    [InlineData("Дополнительный вентилятор", "компл", 0.000001)]
    [InlineData("Материалы", "м²", 2500.123456)]
    public void ValidExtras_AcceptNamesQuantitiesAndUnits(string name, string unit, double qty) =>
        Assert.Null(SqlServerProcurement.ValidateExtra(name, unit, (decimal)qty));

    [Theory]
    [InlineData("", "шт", 1.0)]
    [InlineData(" ", "шт", 1.0)]
    [InlineData("Материал", "", 1.0)]
    [InlineData("Материал", "шт", 0.0)]
    [InlineData("Материал", "шт", -1.0)]
    [InlineData("Материал", "шт", 0.0000001)]
    public void InvalidExtras_CannotBeInserted(string name, string unit, double qty) =>
        Assert.NotNull(SqlServerProcurement.ValidateExtra(name, unit, (decimal)qty));

    [Fact]
    public void CostMustBeNonnegative()
    {
        Assert.NotNull(SqlServerProcurement.ValidateExtra("Зонт", "шт", 1m, -1m));
        Assert.Null(SqlServerProcurement.ValidateExtra("Зонт", "шт", 1m, 0m));
        Assert.Null(SqlServerProcurement.ValidateExtra("Зонт", "шт", 1m, null));
    }

    [Fact]
    public void OversizedNamesAndUnitsAreRejectedBeforeSql()
    {
        Assert.NotNull(SqlServerProcurement.ValidateExtra(new string('А', 256), "шт", 1m));
        Assert.NotNull(SqlServerProcurement.ValidateExtra("Решётка", new string('Ш', 33), 1m));
    }

    [Fact]
    public void InvalidExtraCannotCauseProductionSqlConnection()
    {
        var save = SqlServerProcurement.AddExtra("Server=invalid;Database=Production",
            12, null, "", "шт", -3m, null, "rev");
        Assert.False(save.Saved);
        Assert.Contains("Название", save.Error!);
    }
}
