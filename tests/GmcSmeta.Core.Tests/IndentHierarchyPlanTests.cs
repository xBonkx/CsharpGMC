using GmcSmeta.Core.Domain;
namespace GmcSmeta.Core.Tests;

public sealed class IndentHierarchyPlanTests
{
    private static IndentHierarchyPlan.Row R(long id, long? parent, long order, bool normal = true) =>
        new(id, parent, order, normal);

    [Fact]
    public void IndentKeepsEveryRowAndUsesImmediateRootWithStableTieBreak()
    {
        var plan = IndentHierarchyPlan.Build(new[] { R(5,null,3), R(2,null,1),
            R(3,2,2), R(4,null,3), R(6,null,4) }, 5, true);
        Assert.Null(plan.OldParentId);
        Assert.Equal(4L, plan.NewParentId);
        Assert.Equal(new long[] { 2,3,4,5,6 }, plan.OrderedIds);
    }

    [Fact]
    public void OutdentRetainsOriginalOrderAndOtherComponents()
    {
        var plan = IndentHierarchyPlan.Build(new[] { R(1,null,0), R(2,1,1),
            R(3,1,2), R(4,null,3) }, 2, false);
        Assert.Equal(1L, plan.OldParentId);
        Assert.Null(plan.NewParentId);
        Assert.Equal(new long[] { 1,3,2,4 }, plan.OrderedIds);
    }

    [Fact]
    public void RejectsFirstRootAndRootWithChildren()
    {
        var rows = new[] { R(1,null,0), R(2,null,1), R(3,2,2) };
        Assert.Throws<InvalidOperationException>(() => IndentHierarchyPlan.Build(rows,1,true));
        Assert.Throws<InvalidOperationException>(() => IndentHierarchyPlan.Build(rows,2,true));
    }

    [Fact]
    public void RejectsProtectedParentRatherThanSkippingToEarlierRoot()
    {
        var rows = new[] { R(1,null,0), R(2,null,1,false), R(3,null,2), R(4,2,3) };
        Assert.Throws<InvalidOperationException>(() => IndentHierarchyPlan.Build(rows,3,true));
        Assert.Throws<InvalidOperationException>(() => IndentHierarchyPlan.Build(rows,4,false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RejectsMalformedHierarchyBeforeAnyChange(int kind)
    {
        var bad = kind switch
        {
            0 => new[] { R(1,null,0), R(2,99,1) },
            1 => new[] { R(1,2,0), R(2,1,1) },
            2 => new[] { R(1,null,0), R(2,1,1), R(3,2,2) },
            _ => new[] { R(1,null,0), R(1,null,1) }
        };
        Assert.Throws<InvalidOperationException>(() => IndentHierarchyPlan.Build(bad,1,false));
    }

    [Fact]
    public void MissingSourceAndWrongDirectionRejected()
    {
        var rows = new[] { R(1,null,0), R(2,1,1) };
        Assert.Throws<InvalidOperationException>(() => IndentHierarchyPlan.Build(rows,99,true));
        Assert.Throws<InvalidOperationException>(() => IndentHierarchyPlan.Build(rows,2,true));
        Assert.Throws<InvalidOperationException>(() => IndentHierarchyPlan.Build(rows,1,false));
    }
}
