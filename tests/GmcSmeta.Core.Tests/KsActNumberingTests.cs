using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class KsActNumberingTests
{
    private static SqlServerActs.ActInfo Act(long id,string number,int day,bool signed,long contract) =>
        new(id,number,new DateTime(2026,1,day),null,null,signed,0,0,contract);

    [Fact]
    public void JoinsUnsignedKs3GroupWhenOtherDsAlreadyHasActs()
    {
        var local=new[] { Act(3,"2.1",2,false,11) };
        var all=new[] { Act(1,"1",1,true,10), Act(2,"2.2",2,false,10), local[0] };
        Assert.Equal("2.3",KsActNumbering.NextNumber(local,all,true));
    }

    [Fact]
    public void SignedLastGroupStartsNextKs3InsteadOfJoining()
    {
        var local=new[] { Act(3,"2.1",2,true,11) };
        var all=new[] { Act(1,"1",1,true,10), Act(2,"2.2",2,true,10), local[0] };
        Assert.Equal("3",KsActNumbering.NextNumber(local,all,true));
    }

    [Fact]
    public void StandaloneActDoesNotGainDsSuffix()
    {
        var local=new[] { Act(1,"1",1,true,10),Act(2,"2.1",2,false,10) };
        Assert.Equal("3",KsActNumbering.NextNumber(local,local,false));
    }

    [Fact]
    public void EmptyEstimateStartsWithActOne()
    {
        Assert.Equal("1",KsActNumbering.NextNumber(
            Array.Empty<SqlServerActs.ActInfo>(),Array.Empty<SqlServerActs.ActInfo>(),false));
    }
}
