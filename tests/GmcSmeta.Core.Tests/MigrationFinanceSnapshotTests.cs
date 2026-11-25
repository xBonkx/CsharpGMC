using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class MigrationFinanceSnapshotTests
{
    private static MigrationFinanceSnapshot Sample()
    {
        var contract=new ContractTotals(1,"main","",20m,100m,25m,125m,125m,125m/6m,1);
        var act=new SqlServerActs.ActInfo(2,"1",new DateTime(2026,11,25),null,null,true,1,125m,
            ContractId:1,Ks3Net:104.17m,Vat:20.83m,Materials:100m,Smr:25m,VatRate:20m);
        return new(new Dictionary<int,ContractTotals>{{1,contract}},
            new Dictionary<long,SqlServerActs.ActInfo>{{2,act}},
            new Dictionary<(long,string),MigrationFinanceSnapshot.GroupAmounts>
                {{(1,"1"),new(104.17m,20.83m,125m,104.17m,104.17m,false,1)}});
    }

    [Fact]
    public void EqualIndependentSnapshotsPass()=>Sample().RequireSame(Sample(),"migration");

    [Fact]
    public void OneKopeckOrSubCentDifferenceStopsMigration()
    {
        var before=Sample();
        var contract=before.Contracts[1];
        Assert.Throws<InvalidOperationException>(()=>before.RequireSame(before with
        {Contracts=new Dictionary<int,ContractTotals>{{1,contract with {Total=125.01m}}}},"migration"));
        Assert.Throws<InvalidOperationException>(()=>before.RequireSame(before with
        {Contracts=new Dictionary<int,ContractTotals>{{1,contract with {Vat=contract.Vat+.0001m}}}},"migration"));
    }

    [Fact]
    public void ActAndCumulativeGroupChangesAreNotHiddenBySameGrandTotal()
    {
        var before=Sample();
        Assert.Throws<InvalidOperationException>(()=>before.RequireSame(before with
        {Acts=new Dictionary<long,SqlServerActs.ActInfo>{{2,before.Acts[2] with {Signed=false}}}},"migration"));
        Assert.Throws<InvalidOperationException>(()=>before.RequireSame(before with
        {Groups=new Dictionary<(long,string),MigrationFinanceSnapshot.GroupAmounts>
            {{(1,"1"),before.Groups[(1,"1")] with {SinceStart=104.18m}}}},"migration"));
        Assert.Throws<InvalidOperationException>(()=>before.RequireSame(before with
        {Acts=new Dictionary<long,SqlServerActs.ActInfo>()},"migration"));
    }
}
