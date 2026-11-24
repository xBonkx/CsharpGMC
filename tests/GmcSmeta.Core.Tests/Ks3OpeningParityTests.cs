using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class Ks3OpeningParityTests
{
    private static SqlServerActs.ActInfo Act(long id,string number,int year,int month,int day,
        decimal gross,decimal net) =>
        new(id,number,new DateTime(year,month,day),null,null,true,1,gross,
            10,"Договор",net,gross-net);

    [Fact]
    public void ReadsOpeningFromPythonSqlMetaJsonExactlyToCents()
    {
        var balance=Ks3OpeningBalance.Parse(
            "{\"through\":\"2025-12-31\",\"gross_cents\":1234567}");
        Assert.Equal(new DateTime(2025,12,31),balance.Through);
        Assert.Equal(12345.67m,balance.Gross);
    }

    [Fact]
    public void RejectsCorruptOpeningInsteadOfSilentlyOmittingMoney()
    {
        Assert.Throws<FormatException>(()=>Ks3OpeningBalance.Parse(
            "{\"through\":\"2026-99-99\",\"gross_cents\":123}"));
        Assert.Throws<FormatException>(()=>Ks3OpeningBalance.Parse(
            "{\"through\":\"2026-01-01\"}"));
    }

    [Fact]
    public void DoesNotCountPreviousActsAlreadyWithinOpeningTwice()
    {
        var acts=new[]
        {
            Act(1,"1",2025,12,20,122m,100m),
            Act(2,"2",2026,1,20,244m,200m),
            Act(3,"3.1",2026,2,10,366m,300m),
            Act(4,"3.2",2026,2,12,122m,100m)
        };
        var opening=new Ks3OpeningBalance(new DateTime(2025,12,31),1000m);
        var scope=new SqlServerKsContractScope.Scope(10,"77",
            Array.Empty<SqlServerKsContractScope.Document>(),acts,opening);
        var group=KsGroupReport.Calculate(scope,"3");
        Assert.True(group.GrossCumulative);
        Assert.Equal(1732m,group.SinceStartNet); // 1000 + 244 + 366 + 122
        Assert.Equal(732m,group.YearNet); // only 2026 KS acts; no prior-year opening
        Assert.Equal(400m,group.PeriodNet); // net for this period stays net
    }

    [Fact]
    public void OpeningInSameYearIsAddedToYearAndStart()
    {
        var opening=new Ks3OpeningBalance(new DateTime(2026,1,1),1000m);
        var cumulative=opening.Cumulative(new DateTime(2026,3,1),
            new[]{(new DateTime(2025,12,31),500m),(new DateTime(2026,2,1),200m)},
            new[]{(new DateTime(2026,3,1),300m)});
        Assert.NotNull(cumulative);
        Assert.Equal(1500m,cumulative.Value.SinceStartGross);
        Assert.Equal(1500m,cumulative.Value.YearGross);
    }

    [Fact]
    public void OpeningDoesNotOverrideAnActBeforeImportCutoff()
    {
        var opening=new Ks3OpeningBalance(new DateTime(2026,6,30),1000m);
        Assert.Null(opening.Cumulative(new DateTime(2026,5,30),
            Array.Empty<(DateTime,decimal)>(),new[]{(new DateTime(2026,5,30),100m)}));
    }
}
