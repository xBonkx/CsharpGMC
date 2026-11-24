using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class KsExtraOrderingTests
{
    private static readonly KsExtraOrdering.Row[] Example =
    {
        new(10,false,true),
        new(20,true,false), // hidden: belongs to a different KS
        new(30,true,true),
        new(40,false,true),
        new(50,true,true)
    };

    [Fact]
    public void MoveDownSkipsHiddenExtraButKeepsItsIdentity()
    {
        var order=KsExtraOrdering.Plan(Example,30,1);
        Assert.Equal(new long[] {10,20,40,30,50},order);
    }

    [Fact]
    public void MoveUpAndDownAtBordersAreSafeNoOps()
    {
        var upper=new[] {new KsExtraOrdering.Row(30,true,true),new KsExtraOrdering.Row(10,false,true)};
        Assert.Equal(upper.Select(r=>r.Id),KsExtraOrdering.Plan(upper,30,-1));
        Assert.Equal(Example.Select(r=>r.Id),KsExtraOrdering.Plan(Example,50,1));
    }

    [Fact]
    public void AnchorMovementSupportsBeforeAndAfter()
    {
        var before=KsExtraOrdering.Plan(Example,50,anchorId:10,before:true);
        var after=KsExtraOrdering.Plan(Example,50,anchorId:10,before:false);
        Assert.Equal(new long[]{50,10,20,30,40},before);
        Assert.Equal(new long[]{10,50,20,30,40},after);
    }

    [Fact]
    public void HiddenExtraCannotBeMovedOrUsedAsAnchor()
    {
        Assert.Throws<InvalidOperationException>(()=>KsExtraOrdering.Plan(Example,20,1));
        Assert.Throws<InvalidOperationException>(()=>KsExtraOrdering.Plan(Example,30,anchorId:20));
        Assert.Throws<InvalidOperationException>(()=>KsExtraOrdering.Plan(Example,10,1));
    }

    [Fact]
    public void DuplicatePositionIdsCannotBeReordered()
    {
        var same=new[]{new KsExtraOrdering.Row(1,true,true),
                       new KsExtraOrdering.Row(1,false,true)};
        Assert.Throws<ArgumentException>(()=>KsExtraOrdering.Plan(same,1,1));
    }
}
