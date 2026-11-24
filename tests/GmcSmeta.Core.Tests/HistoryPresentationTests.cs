using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class HistoryPresentationTests
{
    [Theory]
    [InlineData("object", "Объект")]
    [InlineData("contract", "Документ")]
    [InlineData("system", "Система")]
    [InlineData("position", "Строка")]
    [InlineData("procurement", "Закупка")]
    [InlineData("movement", "Склад")]
    [InlineData("act", "Акт КС")]
    [InlineData("shipment", "Ведомость")]
    [InlineData("smr_cost", "Затраты СМР")]
    [InlineData("catalog", "Справочник")]
    [InlineData("metal_register", "Склад металла")]
    [InlineData("person", "Человек")]
    [InlineData("unknown_entity", "unknown_entity")]
    public void EntityLabel_TranslatesKnownValuesAndKeepsUnknownRaw(string entity, string expected) =>
        Assert.Equal(expected, HistoryPresentation.EntityLabel(entity));

    [Theory]
    [InlineData("create", "создано")]
    [InlineData("update", "изменено")]
    [InlineData("edit", "изменено")]
    [InlineData("delete", "удалено")]
    [InlineData("move", "перенесено")]
    [InlineData("import", "загружено из Excel")]
    [InlineData("undo", "отмена (Ctrl+Z)")]
    [InlineData("redo", "возврат (Ctrl+Y)")]
    [InlineData("copy", "скопировано")]
    [InlineData("rename", "переименовано")]
    [InlineData("line", "строка акта")]
    [InlineData("in", "пришло на склад")]
    [InlineData("out", "выдано на объект")]
    [InlineData("back", "возврат с объекта")]
    [InlineData("back_supplier", "возврат поставщику")]
    [InlineData("fix", "выдано на объект")]
    [InlineData("unfix", "выдача отменена")]
    [InlineData("attach", "файл прикреплён")]
    [InlineData("detach", "файл убран")]
    [InlineData("discount", "скидка")]
    [InlineData("archive", "в архив")]
    [InlineData("archive_year", "год архива")]
    [InlineData("merge", "объединено")]
    [InlineData("restore", "возвращено из корзины")]
    [InlineData("sign", "подписан")]
    [InlineData("unsign", "подпись снята")]
    [InlineData("request", "заявка")]
    [InlineData("spec_update", "обновление спецификации")]
    [InlineData("access_list", "список допуска")]
    [InlineData("orders", "приказы")]
    [InlineData("unarchive", "из архива")]
    [InlineData("unknown_action", "unknown_action")]
    public void ActionLabel_TranslatesKnownValuesAndKeepsUnknownRaw(string action, string expected) =>
        Assert.Equal(expected, HistoryPresentation.ActionLabel(action));

    [Theory]
    [InlineData("delete", "delete")]
    [InlineData("undo", "undo")]
    [InlineData("redo", "undo")]
    [InlineData("create", "create")]
    [InlineData("import", "create")]
    [InlineData("update", null)]
    [InlineData("unknown", null)]
    public void TintKey_GroupsRowsLikePython(string action, string? expected) =>
        Assert.Equal(expected, HistoryPresentation.TintKey(action));

    [Fact]
    public void ShortDetails_RemovesFirstMatchingDocumentPrefix()
    {
        var prefixes = new[]
        {
            "КП № 12 от 01.02.2026 — Ремонт / ",
            "КП № 12 от 01.02.2026 — Ремонт: ",
            "Объект / КП № 12 от 01.02.2026 — Ремонт / ",
        };

        Assert.Equal("Строка: цена: <1> → <2>",
            HistoryPresentation.ShortDetails(
                "КП № 12 от 01.02.2026 — Ремонт / Строка: цена: <1> → <2>", prefixes));
        Assert.Equal("Система / Строка",
            HistoryPresentation.ShortDetails(
                "КП № 12 от 01.02.2026 — Ремонт: Система / Строка", prefixes));
        Assert.Equal("Ведомость",
            HistoryPresentation.ShortDetails(
                "Объект / КП № 12 от 01.02.2026 — Ремонт / Ведомость", prefixes));
        Assert.Equal("Без префикса",
            HistoryPresentation.ShortDetails("Без префикса", prefixes));
        Assert.Equal("", HistoryPresentation.ShortDetails(null, prefixes));
    }

    [Fact]
    public void FormatTimestamp_UsesPythonFormatAndEmptyForNull()
    {
        Assert.Equal("05.03.2026 09:07",
            HistoryPresentation.FormatTimestamp(new DateTime(2026, 3, 5, 9, 7, 0)));
        Assert.Equal("", HistoryPresentation.FormatTimestamp(null));
    }

    [Fact]
    public void MatchesWords_RequiresEveryWordInOneLineIgnoringCase()
    {
        const string line = "05.03.2026 09:07 Иванов Строка изменено КП № 12: демонтаж";

        Assert.True(HistoryPresentation.MatchesWords("", line));
        Assert.True(HistoryPresentation.MatchesWords("   ", line));
        Assert.True(HistoryPresentation.MatchesWords("иванов демонтаж", line));
        Assert.True(HistoryPresentation.MatchesWords("СТРОКА изменено", line));
        Assert.True(HistoryPresentation.MatchesWords("кп 12", line));
        Assert.False(HistoryPresentation.MatchesWords("иванов удалено", line));
        Assert.False(HistoryPresentation.MatchesWords("петров", line));
    }

    [Fact]
    public void MatchesWords_EmptyQueryKeepsRowsWithEmptyOrUnknownText()
    {
        Assert.True(HistoryPresentation.MatchesWords(null, null));
        Assert.False(HistoryPresentation.MatchesWords("что-то", null));
        Assert.True(HistoryPresentation.MatchesWords("", ""));
    }
}
