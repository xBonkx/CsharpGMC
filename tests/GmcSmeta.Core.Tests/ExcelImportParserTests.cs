using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ExcelImportParserTests
{
    private static IReadOnlyList<object?> Row(params object?[] values) => values;
    private static ExcelImportResult Parse(params IReadOnlyList<object?>[] rows) =>
        ExcelImportParser.ParseWorkbook("смета.xlsx",
            new List<(string Sheet, IReadOnlyList<IReadOnlyList<object?>> Rows)>
            { ("Лист1", rows) });

    [Fact]
    public void SimpleEstimate_PreservesSectionMoneyAndSourceRows()
    {
        var book = Parse(
            Row("КП №1"),
            Row("№", "Наименование", "Ед.", "Кол-во", "Цена", "Всего"),
            Row(null, "П1", null, null, null, null),
            Row(1, "Воздуховод", "м", 2, 150.25, 300.50),
            Row(null, "Итого", null, null, null, 300.50));
        var block = Assert.Single(book.Blocks);
        Assert.True(block.Ok);
        Assert.Equal(1, block.PositionCount);
        Assert.Equal(300.50m, block.MaterialSum);
        Assert.Equal(300.50m, block.DeclaredTotal);
        var position = Assert.Single(Assert.Single(block.Sections).Positions);
        Assert.Equal("Воздуховод", position.Name);
        Assert.Equal(4, position.SourceRow);
    }

    [Fact]
    public void TwoLineHeader_SeparatesMaterialsAndWorkPrices()
    {
        var result = Parse(
            Row("№", "Наименование", "Ед.", "Кол-во", "Стоимость материалов", null, "Стоимость работ", null),
            Row(null, null, null, null, "за ед. изм", "Всего", "за ед. изм", "Всего"),
            Row(1, "Монтаж", "шт", 2, 100, 200, 20, 40),
            Row(null, "Итого", null, null, null, 240, null, null));
        var block = Assert.Single(result.Blocks);
        Assert.True(block.HasWork);
        Assert.Equal(200m, block.MaterialSum);
        Assert.Equal(40m, block.WorkSum);
        Assert.Equal(240m, block.CalculatedTotal);
        Assert.True(block.Ok);
        Assert.Equal(20m, block.Sections[0].Positions[0].WorkPrice);
    }

    [Fact]
    public void MultipleDocumentsOnSameSheet_AreSeparated()
    {
        var result = Parse(
            Row("№", "Наименование", "Ед.", "Кол-во", "Цена", "Всего"),
            Row(1, "Позиция A", "шт", 1, 15, 15),
            Row(null, "Итого", null, null, null, 15),
            Row(null, "Система В2", null, null, null, null),
            Row(1, "Позиция B", "шт", 1, 30, 30),
            Row(null, "Итого", null, null, null, 30));
        Assert.Equal(2, result.Blocks.Count);
        Assert.Equal(15m, result.Blocks[0].MaterialSum);
        Assert.Equal(30m, result.Blocks[1].MaterialSum);
    }

    [Fact]
    public void ChildrenPurchasePricesAndExtraColumns_ArePreserved()
    {
        var result = Parse(
            Row("№", "Наименование", "Ед.", "Кол-во", "Цена закупки", "Сумма закупки", "Цена розница", "Всего", "Артикул"),
            Row(null, "В1", null, null, null, null, null, null, null),
            Row(1, "Комплект", "шт", 2, 10, 20, 50, 100, "K-42"),
            Row(null, "Шуруп", "шт", 4, null, null, null, null, "M4"),
            Row(null, "Итого", null, null, null, null, null, 100, null));
        var block = Assert.Single(result.Blocks);
        Assert.Equal(2, block.PositionCount);
        Assert.True(block.HasPurchase);
        var pos = Assert.Single(block.Sections[0].Positions);
        Assert.Equal(10m, pos.PurchasePrice);
        Assert.Equal(50m, pos.Price);
        Assert.Equal("K-42", pos.Extra["I"]);
        var child = Assert.Single(pos.Children);
        Assert.Equal("Шуруп", child.Name);
        Assert.Equal("M4", child.Extra["I"]);
        Assert.True(block.Ok);
    }

    [Fact]
    public void MalformedQuantity_ProducesWarningAndBlocksAutomaticConfirmation()
    {
        var result = Parse(
            Row("Наименование", "Ед.", "Кол-во", "Цена", "Всего"),
            Row("Позиция", "шт", "не число", 100, 0));
        var block = Assert.Single(result.Blocks);
        Assert.Equal(0m, block.Sections[0].Positions[0].Quantity);
        Assert.Contains(block.Warnings, w => w.Contains("количество"));
        Assert.False(block.Ok);
    }

    [Fact]
    public void SeparateWorksheets_AreKeptDistinct()
    {
        IReadOnlyList<IReadOnlyList<object?>> A(string name) =>
            new IReadOnlyList<object?>[]
            {
                Row("Наименование", "Ед.", "Кол-во", "Цена", "Всего"),
                Row(name, "шт", 1, 10, 10),
                Row("Итого", null, null, null, 10)
            };
        var result = ExcelImportParser.ParseWorkbook("book.xlsm",
            new List<(string Sheet, IReadOnlyList<IReadOnlyList<object?>> Rows)>
            { ("КП1", A("Первое")), ("КП2", A("Второе")) });
        Assert.Equal(new[] { "КП1", "КП2" }, result.Blocks.Select(b => b.Sheet));
    }

    [Theory]
    [InlineData(2500)]
    [InlineData(10000)]
    public void LargeEstimates_KeepExactRowsAndTotals(int count)
    {
        var rows = new List<IReadOnlyList<object?>>
        {
            Row("№", "Наименование", "Ед.", "Кол-во", "Цена", "Всего")
        };
        for (var i = 1; i <= count; i++)
            rows.Add(Row(i, "Позиция " + i, "шт", 1, 1.25, 1.25));
        rows.Add(Row(null, "Итого", null, null, null, count * 1.25));
        var result = ExcelImportParser.ParseWorkbook("large.xlsx",
            new List<(string Sheet, IReadOnlyList<IReadOnlyList<object?>> Rows)> { ("Смета", rows) });
        var b = Assert.Single(result.Blocks);
        Assert.Equal(count, b.PositionCount);
        Assert.Equal(count * 1.25m, b.MaterialSum);
        Assert.True(b.Ok);
    }

    [Fact]
    public void UnsafeNumbers_DoNotBecomeNaNOrHugePrices()
    {
        var result = Parse(
            Row("Наименование", "Ед.", "Кол-во", "Цена", "Всего"),
            Row("Позиция", "шт", 1, "1e308", null),
            Row("Итого", null, null, null, 0));
        var b = Assert.Single(result.Blocks);
        Assert.Equal(0m, b.Sections[0].Positions[0].Price);
        Assert.Contains(b.Warnings, s => s.Contains("Нет цен"));
    }
    [Fact]
    public void DnsCustomerForm_KeepsBasisAndVatOnTop()
    {
        var result = Parse(
            Row("Сметный расчёт на систему вентиляции"),
            Row("№ п/п", "Обоснование", "Наименование работ и затрат", "Ед.", "Количество", "Стоимость работ", "Стоимость материала"),
            Row(null, null, null, null, null, "Работы за ед", "Материал за ед"),
            Row("Раздел 1. Система ВД1", null, null, null, null, null, null),
            Row(1, "ТЕР-01", "Работа", "шт", 2, 10, null),
            Row(2, "М-11", "Материал", "шт", 1, null, 80),
            Row("НДС 22%", null, null, null, null, null, 22),
            Row("Итого по сметному расчёту", null, null, null, null, null, 122));
        var block = Assert.Single(result.Blocks);
        Assert.Equal("dns", block.Form);
        Assert.Equal(100m, block.MaterialSum);
        Assert.Equal(122m, block.CalculatedTotal);
        Assert.True(block.Ok);
        Assert.Equal("ТЕР-01", block.Sections[0].Positions[0].Extra["basis"]);
        Assert.Equal("work", block.Sections[0].Positions[0].Extra["kind"]);
    }

    [Fact]
    public void RevitQuantitiesOnly_DetectsSystems()
    {
        var result = Parse(
            Row("Спецификация материалов и оборудования"),
            Row("В1", null, null, null, null, null, null),
            Row("-", "Воздуховод", "Оц 0,7", "", "Завод", "шт", 2),
            Row("-", "Отвод", "90°", "", "Завод", "шт", 3),
            Row("П1", null, null, null, null, null, null),
            Row("-", "Решётка", "ДР", "", "Завод", "шт", 1));
        var block = Assert.Single(result.Blocks);
        Assert.True(block.VolumesOnly);
        Assert.True(block.Ok);
        Assert.Equal(3, block.PositionCount);
        Assert.Equal(2, block.Sections.Count);
        Assert.Equal(0m, block.MaterialSum);
        Assert.Equal("Воздуховод Оц 0,7", block.Sections[0].Positions[0].Name);
    }

    [Fact]
    public void StandardVatOnTop_ReconcilesGrossTotal()
    {
        var result = Parse(
            Row("Наименование", "Ед.", "Кол-во", "Цена", "Всего"),
            Row("Труба", "м", 1, 100, 100),
            Row("Итого без НДС", null, null, null, 100),
            Row("НДС 22%", null, null, null, 22),
            Row("Итого с НДС", null, null, null, 122));
        var block = Assert.Single(result.Blocks);
        Assert.Equal("dns", block.Form);
        Assert.Equal(122m, block.CalculatedTotal);
        Assert.Equal(122m, block.DeclaredTotal);
        Assert.True(block.Ok);
    }

    [Fact]
    public void FinancialTotals_RoundOnceAcrossAllSections()
    {
        var block = new ExcelImportBlock();
        foreach (var name in new[] { "П1", "В1" })
        {
            var section = new ExcelImportSection { Name = name };
            section.Positions.Add(new ExcelImportPosition { Name = "Позиция", Unit = "шт",
                Quantity = 1m, Price = 0.004m });
            block.Sections.Add(section);
            Assert.Equal(0m, section.MaterialSum);
        }
        // Python PBlock.material_sum sums raw products (0.004 + 0.004), then rounds.
        Assert.Equal(0.01m, block.MaterialSum);
    }

    [Fact]
    public void DnsFinancialTotals_UseExcelHalfUpPerRow()
    {
        var block = new ExcelImportBlock { Form = "dns", VatRate = 22m };
        var section = new ExcelImportSection { Name = "Раздел" };
        section.Positions.Add(new ExcelImportPosition { Name = "A", Unit = "шт",
            Quantity = 1m, Price = 0.005m });
        section.Positions.Add(new ExcelImportPosition { Name = "B", Unit = "шт",
            Quantity = 1m, Price = 0.005m });
        block.Sections.Add(section);
        Assert.Equal(0.01m, block.MaterialSum);
        Assert.Equal(0.02m, block.DnsRowsSum);
        Assert.Equal(0.02m, block.CalculatedTotal);
    }

    [Fact]
    public void StandardCustomerForm_PreservesBasisForKs2()
    {
        var result = Parse(
            Row("№", "Обоснование", "Наименование", "Ед.", "Кол-во", "Цена", "Всего"),
            Row(1, "М9.14.8.0020", "Клапан противопожарный", "шт", 2, 100, 200),
            Row(null, null, "Итого", null, null, null, 200));
        var block = Assert.Single(result.Blocks);
        var pos = Assert.Single(block.Sections[0].Positions);
        Assert.Equal("М9.14.8.0020", pos.Extra["basis"]);
        Assert.Equal(200m, block.MaterialSum);
        Assert.True(block.Ok);
    }


    [Fact]
    public void Python0668_SectionTotalsAndUnpricedRows()
    {
        var book=Parse(
            Row("Приложение №1 к Дополнительному соглашению №3 от 08.04.2026 г."),
            Row("Поз.","Наименование и техническая характеристика","Ед. изм.","Коли-чество","Цена","Всего"),
            Row(1,"Раздел 1 - прокладка воздуховодов по фасаду"),
            Row(2,"Воздуховод оцинк. б=0,8мм","м2",1460.8m,1450m,2118160m),
            Row(3,"Расходные материалы","компл.",1m,300000m,300000m),
            Row(4,"ИТОГО ЗА МАТЕРИАЛ:",null,null,null,2418160m),
            Row(5,"ТРАНСПОРТНЫЕ РАСХОДЫ:",null,null,null,250000m),
            Row(6,"СМР:",null,null,null,3450000m),
            Row(7,"ИТОГО, с учетом НДС 22%:",null,null,null,6118160m),
            Row(8,"Раздел 2 - покраска декоративных решеток"),
            Row(9,"Решетка декоративная 1100х400","шт",26,"*","*"),
            Row(10,"Окрашивание решетки в RAL 7024","шт",208,null,280000m),
            Row(11,"ИТОГО, с учетом НДС 22%:",null,null,null,280000m),
            Row(12,"Всего по разделам Р.1,2",null,null,null,6398160m));
        var block=Assert.Single(book.Blocks);
        Assert.True(block.Ok,string.Join("; ",block.Checks.Select(c=>c.Description)));
        Assert.Equal(4,block.PositionCount);
        Assert.Equal(2698160m,block.DeclaredMaterial);
        Assert.Equal(250000m,block.Transport);
        Assert.Equal(3450000m,block.Smr);
        Assert.Equal(6398160m,block.CalculatedTotal);
        Assert.Equal("dop",block.Kind);
        Assert.Equal("3",block.Number);
        Assert.Equal("08.04.2026",block.Date);
        var positions=block.Sections.SelectMany(s=>s.Positions).ToList();
        Assert.Equal(0m,positions.Single(p=>p.Name.StartsWith("Решетка")).Price);
        var painted=positions.Single(p=>p.Name.StartsWith("Окрашивание"));
        Assert.Equal(280000m,decimal.Round(painted.Price*painted.Quantity,2));
    }

    [Theory]
    [InlineData(45,true)]
    [InlineData(47,false)]
    public void Python0670_NumberedSectionRangeMustMatch(int rangeTotal,bool merged)
    {
        var book=Parse(
            Row("№","Наименование","Ед.","Кол-во","Цена","Всего"),
            Row(null,"Раздел 1"),
            Row(1,"Позиция A","шт",1,15,15),
            Row(null,"ВСЕГО ПО Р.1 С НДС 22%",null,null,null,15),
            Row(null,"Раздел 2"),
            Row(2,"Позиция B","шт",1,30,30),
            Row(null,"ВСЕГО ПО Р.2 С НДС 22%",null,null,null,30),
            Row(null,"ВСЕГО ПО Р.1-Р.2 С НДС 22%",null,null,null,rangeTotal));
        Assert.Equal(merged?1:2,book.Blocks.Count);
        Assert.Equal(45m,book.Blocks.Sum(b=>b.CalculatedTotal));
    }
}
