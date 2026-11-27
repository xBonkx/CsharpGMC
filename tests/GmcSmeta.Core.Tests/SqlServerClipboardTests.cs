using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

/// <summary>Pure, offline regression checks for Excel paste and SQL Server input guards.</summary>
public sealed class SqlServerClipboardTests
{
    [Fact]
    public void ParseTsv_PlainRowsAndOptionalFields()
    {
        var rows = PastedPositions.Parse("Наименование\tЕд.\tКол-во\tЦена\tЗакупка\tПримечание\r\n" +
            "Воздуховод\tм2\t12,5\t1 200,50\t800\tоцинковка\r\n" +
            "Отвод\tшт\t3\t150\t\t");
        Assert.Equal(2, rows.Count);
        Assert.Equal(12.5m, rows[0].Qty);
        Assert.Equal(1200.50m, rows[0].Price);
        Assert.Equal(800m, rows[0].PurchasePrice);
        Assert.Null(rows[1].PurchasePrice);
        Assert.Equal("Отвод", rows[1].Name);
    }

    [Fact]
    public void ParseTsv_NameOnlyEightRows_PreservesOrderAndDefaults()
    {
        var names = new[] { "Канальный вентилятор", "Преобразователь частоты", "Гибкая вставка",
            "Клапан 100x100", "Клапан 200x100", "Клапан 300x200",
            "Клапан противопожарный 100x100", "Клапан противопожарный 200x100" };
        var rows = PastedPositions.Parse(string.Join("\r\n", names));
        Assert.Equal(names, rows.Select(row => row.Name).ToArray());
        Assert.All(rows, row =>
        {
            Assert.Equal("шт", row.Unit);
            Assert.Equal(1m, row.Qty);
            Assert.Equal(0m, row.Price);
            Assert.Null(row.PurchasePrice);
        });
    }

    [Fact]
    public void ParseTsv_QuotedCellsPreserveNewlinesAndQuotes()
    {
        var rows = PastedPositions.Parse("\"Клапан\tуниверсальный\"\tшт\t1\t10\t\t\"строка 1\r\n\"\"тест\"\"\"");
        Assert.Single(rows);
        Assert.Equal("Клапан\tуниверсальный", rows[0].Name);
        Assert.Equal("строка 1\r\n\"тест\"", rows[0].Note);
    }

    [Theory]
    [InlineData("Позиция\tшт\t-1\t10")]
    [InlineData("Позиция\tшт\tabc\t10")]
    [InlineData("Позиция\tшт\t1\t-100")]
    [InlineData("\tшт\t2\t100")]
    [InlineData("Позиция\tшт\t1\t10\t\t\textra")]
    [InlineData("\"Кавычка без конца\tшт")]
    public void ParseTsv_RejectsInvalidTableWithoutDatabase(string text) =>
        Assert.Throws<FormatException>(() => PastedPositions.Parse(text));

    [Fact]
    public void ParseTsv_RejectsMoreThan5000Rows()
    {
        var input = string.Concat(Enumerable.Repeat("Товар\tшт\t1\t10\n", 5001));
        Assert.Throws<FormatException>(() => PastedPositions.Parse(input));
    }

    [Fact]
    public void DeleteSection_BadIdsAreRejectedBeforeConnection()
    {
        var result = SqlServerDatabase.DeleteSection("not-a-connection", -1, 10, "v1");
        Assert.False(result.Saved);
        Assert.Equal("v1", result.Revision);
    }

    [Fact]
    public void MovePositionToSection_RejectsSameSectionBeforeConnection()
    {
        var result = SqlServerDatabase.MovePositionToSection("not-a-connection", 1, 2, 2, 10, "v1");
        Assert.False(result.Saved);
        Assert.Contains("другой раздел", result.Error);
    }

    [Fact]
    public void PastePositions_RejectsEmptyBatchBeforeConnection()
    {
        var result = SqlServerDatabase.PastePositions("not-a-connection", 1, 1, null,
            Array.Empty<PastedPosition>(), "v1");
        Assert.False(result.Saved);
    }
}
