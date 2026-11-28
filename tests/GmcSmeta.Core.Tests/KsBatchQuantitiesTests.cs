using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class KsBatchQuantitiesTests
{
    private static KsBatchPosition P(long id, decimal now = 0, bool extra = false, bool linked = false) =>
        new(id,10,3,now,100,extra,linked);

    [Fact]
    public void FillRemainingSkipsLinkedOutsideAndUnchangedRows()
    {
        Assert.Equal(new[]{(1L,7m)},KsBatchQuantities.FillRemaining(
            new[]{P(1),P(2,7),P(3,extra:true),P(4,linked:true),P(5,6.999999m)}));
    }

    [Fact]
    public void ClearKeepsExistingSelectionSemanticsIncludingLinkedAndOutside()
    {
        Assert.Equal(new[]{(2L,0m),(3L,0m)},KsBatchQuantities.Clear(
            new[]{P(1),P(2,2,extra:true),P(3,1,linked:true)}));
    }

    [Fact]
    public void FillIssuedSubtractsPreviouslyClosedAndCapsRemaining()
    {
        var issued = new Dictionary<long,decimal>{{1,5},{2,99},{3,2},{4,5},{5,5}};
        Assert.Equal(new[]{(1L,2m),(2L,7m),(3L,0m)},KsBatchQuantities.FillIssued(
            new[]{P(1),P(2),P(3,1),P(4,extra:true),P(5,linked:true),P(6)},issued));
    }

    [Fact]
    public void MissingIssueAndUnchangedRowsProduceNoWrite()
    {
        Assert.Empty(KsBatchQuantities.FillIssued(new[]{P(1),P(2,2)},new Dictionary<long,decimal>{{2,5}}));
    }
}
