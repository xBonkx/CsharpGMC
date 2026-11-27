using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class StageTableSearchTests
{
    [Theory]
    [InlineData("КЛАПАН П1", 1)]
    [InlineData("100%", 1)]
    [InlineData("[А]", 1)]
    [InlineData("O'Neil", 1)]
    [InlineData("*", 1)]
    [InlineData("   ", 3)]
    [InlineData("нет", 0)]
    public void LiteralFilterKeepsExactDataAndDoesNotRebuildRows(string query, int expected)
    {
        var table = new DataTable { Locale = CultureInfo.InvariantCulture, CaseSensitive = true };
        table.Columns.Add(StageTableSearch.Column, typeof(string));
        table.Columns.Add("Exact", typeof(decimal));
        table.Rows.Add("п1 · клапан [а] 100% · шт", 1.234567m);
        table.Rows.Add("п2 · o'neil * · м", 2m);
        table.Rows.Add("п3 · решетка 100 · шт", 3m);
        var view = table.DefaultView;
        view.RowFilter = StageTableSearch.Expression(query);
        Assert.Equal(expected, view.Count);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(1.234567m, (decimal)table.Rows[0]["Exact"]);
    }
}
