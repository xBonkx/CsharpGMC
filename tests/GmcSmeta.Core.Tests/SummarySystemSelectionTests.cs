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

    [Fact]
    public void LabelsUseActualParentNotAdjacentSystem()
    {
        var labels = SummarySystemSelection.Labels([(1L, "П1"), (5L, "В1"), (2L, "Раздел")], Parents());
        Assert.Equal("П1 › Раздел", labels[2]);
        Assert.Equal("В1", labels[5]);
    }
}
