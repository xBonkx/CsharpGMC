using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class SummarySystemSelectionTests
{
    private static Dictionary<long, long> Parents() => new() { [2] = 1, [3] = 1, [4] = 5 };

    [Fact]
    public void NoSelectionMeansAll() => Assert.Null(SummarySystemSelection.Expand([], Parents()));

    [Fact]
    public void MultipleParentsIncludeChildrenWithoutDuplicates()
    {
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 },
            SummarySystemSelection.Expand([1, 2, 5], Parents())!.OrderBy(id => id).ToArray());
    }

    [Fact]
    public void SelectingChildDoesNotSelectParentOrSiblings()
    {
        Assert.Equal(new long[] { 2 }, SummarySystemSelection.Expand([2], Parents())!.ToArray());
    }

    [Theory]
    [InlineData("ВЕ12", "ВЕ")]
    [InlineData("В1.4 (с/у)", "В1")]
    [InlineData("ДП1.2 лш", "ДП1")]
    [InlineData("ве12", "ВЕ")]
    [InlineData("123", "")]
    public void FamilyMatchesPython(string label, string family) =>
        Assert.Equal(family, SummarySystemSelection.Family(label));

    [Fact]
    public void SearchMatchesAllWordsIgnoringCaseAndWhitespace()
    {
        Assert.True(SummarySystemSelection.MatchesSearch("В1 › Санузел", "санУЗЕЛ\tВ1"));
        Assert.False(SummarySystemSelection.MatchesSearch("В1 › Санузел", "В1 кухня"));
        Assert.True(SummarySystemSelection.MatchesSearch("В1", " "));
    }

    [Fact]
    public void RowIdentityIgnoresSortButRejectsChangedGroupMembership()
    {
        var key = SummarySystemSelection.RowKey("system", 1, null, [5, 7]);
        Assert.Equal(key, SummarySystemSelection.RowKey("system", 1, null, [7, 5]));
        Assert.NotEqual(key, SummarySystemSelection.RowKey("system", 1, null, [5]));
        Assert.NotEqual(key, SummarySystemSelection.RowKey("system", 0, "1", [5, 7]));
        Assert.NotEqual(SummarySystemSelection.RowKey("pos", 1, null, [5]),
            SummarySystemSelection.RowKey("pos", 1, null, [5, 7]));
        Assert.Null(SummarySystemSelection.RowKey("extra", 0, null, []));
    }

    [Fact]
    public void LabelsUseActualParentNotAdjacentSystem()
    {
        var labels = SummarySystemSelection.Labels([(1L, "П1"), (5L, "В1"), (2L, "Раздел")], Parents());
        Assert.Equal("П1 › Раздел", labels[2]);
        Assert.Equal("В1", labels[5]);
    }
}
