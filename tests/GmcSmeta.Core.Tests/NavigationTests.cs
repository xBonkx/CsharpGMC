using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public class NavigationTests
{
    private static readonly ObjectRow[] Objects =
    {
        new(1, "Вентиляция", "", "", false, ""),
        new(2, "Пустой объект", "", "", false, ""),
        new(3, "Завершённый объект", "", "", true, "2025-01-01")
    };
    private static DocRow Doc(int id, int obj, string kind) =>
        new(id, obj, kind, "", "", "", "", "", false, 0, 0, 0, 0);
    private static readonly DocRow[] Docs = { Doc(1, 1, "kp"), Doc(2, 1, "main"), Doc(3, 1, "dop"), Doc(4, 3, "main") };

    [Fact]
    public void Contracts_ShowsMainAndDopWithoutKpOrArchive()
    {
        var group = Assert.Single(Navigation.Select(Objects, Docs, NavigationMode.Contracts));
        Assert.Equal(1, group.Object.Id);
        Assert.Equal(new[] { 2, 3 }, group.Documents.Select(d => d.Id));
    }

    [Fact]
    public void Kp_ShowsProposalsAndNewEmptyObjects()
    {
        var groups = Navigation.Select(Objects, Docs, NavigationMode.Kp);
        Assert.Equal(new[] { 1, 2 }, groups.Select(g => g.Object.Id));
        Assert.Equal(1, Assert.Single(groups[0].Documents).Id);
        Assert.Empty(groups[1].Documents);
    }

    [Fact]
    public void Archive_ContainsOnlyArchivedObjects()
    {
        var group = Assert.Single(Navigation.Select(Objects, Docs, NavigationMode.Archive));
        Assert.Equal(3, group.Object.Id);
        Assert.Equal(4, Assert.Single(group.Documents).Id);
    }

    [Fact]
    public void Search_IsCaseInsensitiveAndPreservesMode()
    {
        var group = Assert.Single(Navigation.Select(Objects, Docs, NavigationMode.Kp, " ВЕНТ "));
        Assert.Equal(1, group.Object.Id);
        Assert.Equal(1, Assert.Single(group.Documents).Id);
        Assert.Empty(Navigation.Select(Objects, Docs, NavigationMode.Contracts, "Завершённый"));
    }

    [Fact]
    public void Years_UseObjectCreationAndArchiveCompletionNotDocumentDate()
    {
        var objects = new[]
        {
            Objects[0] with { CreatedAt = "2024-12-01 00:00:00" },
            Objects[1] with { CreatedAt = "2026-01-01" },
            Objects[2] with { CreatedAt = "2020-01-01", ArchivedOn = "2025-01-01" }
        };
        Assert.Equal(new[] { "2026", "2024" }, Navigation.Years(objects, Docs, NavigationMode.Kp, 2026).Select(y => y.Year));
        Assert.Equal("2025", Assert.Single(Navigation.Years(objects, Docs, NavigationMode.Archive, 2026)).Year);
        Assert.Equal("Без года", Assert.Single(Navigation.Years(new[] { objects[2] with { ArchivedOn = "" } }, Docs, NavigationMode.Archive, 2026)).Year);
    }

    [Fact]
    public void Stages_AreNestedSortedAndRemainderIsLast()
    {
        var docs = new[]
        {
            Doc(1, 1, "kp"),
            Doc(4, 1, "kp") with { StageOf = 1, StageNo = 1, StageRest = true },
            Doc(3, 1, "kp") with { StageOf = 1, StageNo = 2 },
            Doc(2, 1, "kp") with { StageOf = 1, StageNo = 1 },
            Doc(5, 1, "kp") with { StageOf = 999 }
        };
        var roots = Navigation.Documents(docs);
        Assert.Equal(new[] { 1, 5 }, roots.Select(n => n.Document.Id));
        Assert.Equal(new[] { 2, 3, 4 }, roots[0].Children.Select(n => n.Document.Id));
    }

    [Theory]
    [InlineData(false, false, false, false, "File", false)]
    [InlineData(false, false, true, false, "Parent", false)]
    [InlineData(true, true, false, false, "Stage", false)]
    [InlineData(true, true, false, true, "Remainder", true)]
    [InlineData(true, false, false, false, "File", false)]
    [InlineData(true, false, false, true, "File", false)]
    [InlineData(true, false, true, true, "Parent", false)]
    [InlineData(true, true, true, false, "Stage", false)]
    public void NavigationHeader_UsesVisibleHierarchy(bool hasStageOf, bool nested,
        bool hasChildren, bool remainder, string role, bool italic)
    {
        var doc = Doc(7, 1, "kp") with { StageOf = hasStageOf ? 1 : null, StageRest = remainder };
        var header = new NavigationDocumentHeader(doc, nested, hasChildren);
        Assert.Equal(role, header.IconRole);
        Assert.Equal(italic, header.IsItalic);
        Assert.Same(doc, header.Document);
    }

    [Fact]
    public void Search_MatchesVisibleDocumentsButKeepsWholeObject()
    {
        var docs = new[] { Docs[0] with { Title = "КП № ABC — Дымоудаление" }, Docs[1] };
        var group = Assert.Single(Navigation.Select(Objects, docs, NavigationMode.Kp, "дымоудаление"));
        Assert.Equal(1, group.Object.Id);
        Assert.Empty(Navigation.Select(Objects, docs, NavigationMode.Contracts, "ABC"));
    }

    [Fact]
    public void Search_MultipleWordsMatchObjectAndDocumentRegardlessOfOrder()
    {
        var objects = new[] { Objects[0] with { Name = "Вентиляция торгового центра" } };
        var docs = new[]
        {
            Docs[0] with { Title = "КП № 19 — Дымоудаление" },
            Docs[1] with { Title = "Договор № 88 — Обслуживание" }
        };

        Assert.Single(Navigation.Select(objects, docs, NavigationMode.Kp, "ДЫМ   вентиляция"));
        Assert.Single(Navigation.Select(objects, docs, NavigationMode.Kp,
            "центра\tДЫМОУДАЛЕНИЕ\nторгового"));
        Assert.Single(Navigation.Select(objects, docs, NavigationMode.Contracts,
            "обслуживание ВЕНТ"));
        // A КП title must not leak into the contracts tab.
        Assert.Empty(Navigation.Select(objects, docs, NavigationMode.Contracts,
            "вент дымоудаление"));
    }

    [Fact]
    public void Search_MultipleWordsCannotCombineDifferentDocuments()
    {
        var docs = new[]
        {
            Docs[0] with { Title = "КП № 1 — Пожарная сигнализация" },
            Docs[0] with { Id = 9, Title = "КП № 2 — Дымоудаление" }
        };

        Assert.Empty(Navigation.Select(new[] { Objects[0] }, docs,
            NavigationMode.Kp, "пожарная дымоудаление"));
        Assert.Single(Navigation.Select(new[] { Objects[0] }, docs,
            NavigationMode.Kp, "пожарная сигнализация"));
    }

    [Theory]
    [InlineData(" 28.10.2026 ", "28.10")]
    [InlineData("28.10.25", "28.10.25")]
    [InlineData("28.10.2025", "28.10.25")]
    [InlineData("как введено", "как введено")]
    [InlineData("", "")]
    public void DateLabel_MatchesPython(string input, string expected) =>
        Assert.Equal(expected, Navigation.ShortDate(input, 2026));

    [Theory]
    [InlineData("")]
    [InlineData("ВЕНТ")]
    [InlineData("ВЕНТ альфа")]
    [InlineData("альфа бета")]
    [InlineData("альфа")]
    [InlineData("пустой")]
    [InlineData("завершённый")]
    public void SearchScratchBufferPreservesScopeAndSingleDocumentMatching(string query)
    {
        var docs = new[] { Doc(1, 1, "kp") with { Name = "альфа" },
            Doc(2, 1, "kp") with { Name = "бета" }, Docs[1], Docs[3] };
        foreach (var mode in new[] { NavigationMode.Kp, NavigationMode.Contracts, NavigationMode.Archive })
        {
            var index = new NavigationSearchIndex(Navigation.Select(Objects, docs, mode));
            var expected = Navigation.Select(Objects, docs, mode, query).Select(g => g.Object.Id).OrderBy(id => id);
            Assert.Equal(expected, index.Match(query).OrderBy(id => id));
            Assert.Equal(expected, index.Match(query).OrderBy(id => id));
        }
    }
}
