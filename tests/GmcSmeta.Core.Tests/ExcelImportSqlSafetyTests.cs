using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ExcelImportSqlSafetyTests
{
    [Theory]
    [InlineData("Server=localhost;Database=master;Integrated Security=true")]
    [InlineData("Server=localhost;Database=RealGmcSmeta;Integrated Security=true")]
    [InlineData("Server=localhost;Database=GmcSmetaProduction;Integrated Security=true")]
    public void NewDocumentImport_NeverWritesOutsidePilot(string cs)
    {
        var block = new ExcelImportBlock { Sheet = "Смета" };
        var selection = new ExcelImportChoice(block, "kp", "", "", "");
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerExcelImport.Import(cs, null, "Объект", "input.xlsx", new[] { selection }, "rev"));
    }

    [Theory]
    [InlineData("Server=localhost;Database=master;Integrated Security=true")]
    [InlineData("Server=localhost;Database=LiveCompanyDB;Integrated Security=true")]
    public void AppendImport_NeverWritesOutsidePilot(string cs)
    {
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerExcelAppend.Append(cs, 1, "input.xlsx", Array.Empty<ExcelImportBlock>(), "rev"));
    }

    [Fact]
    public void NegativeValues_AreRejectedBeforeOpeningPilotConnection()
    {
        var block = new ExcelImportBlock { Sheet = "Смета", FirstRow = 2, LastRow = 4 };
        var section = new ExcelImportSection { Name = "П1" };
        section.Positions.Add(new ExcelImportPosition
        {
            Name = "Позиция с ошибкой", Unit = "шт", Quantity = -1, Price = 100
        });
        block.Sections.Add(section);
        const string cs = "Server=localhost;Database=GmcSmetaPilot_Unreachable;Integrated Security=true";
        Assert.Throws<ArgumentException>(() =>
            SqlServerExcelAppend.Append(cs, 1, "input.xlsx", new[] { block }, "rev"));
        Assert.Throws<ArgumentException>(() =>
            SqlServerExcelImport.Import(cs, null, "Объект", "input.xlsx",
                new[] { new ExcelImportChoice(block, "kp", "", "", "Смета") }, "rev"));
    }
}
