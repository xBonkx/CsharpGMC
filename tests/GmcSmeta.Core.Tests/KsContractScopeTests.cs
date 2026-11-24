using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class KsContractScopeTests
{
    private static readonly SqlServerKsContractScope.Document Main =
        new(1,10,"main","A-123","Основной договор",null,"",22m,"");
    private static readonly SqlServerKsContractScope.Document Ds =
        new(2,10,"dop","1","ДС №1",1,"",22m,"");
    private static readonly SqlServerKsContractScope.Document Other =
        new(3,10,"main","B-555","Другой договор",null,"",22m,"");

    [Fact]
    public void ExplicitBaseContractGroupsDsWithMain()
    {
        var all = new[] { Main, Ds, Other };
        Assert.Equal("A-123", SqlServerKsContractScope.EffectiveNumber(Ds, all));
        Assert.Equal(new long[] {1,2},
            SqlServerKsContractScope.ResolveDocuments(2,all).Select(x=>x.Id));
    }

    [Fact]
    public void NeverLeaksContractsAcrossObjectsEvenWithMatchingNumbers()
    {
        var foreign = Main with { Id = 44, ObjectId = 99 };
        var results = SqlServerKsContractScope.ResolveDocuments(1,
            new[] { Main, Ds, Other, foreign });
        Assert.DoesNotContain(results,x=>x.ObjectId==99);
    }

    [Fact]
    public void UnlinkedDsUsesSingleMainContractFallback()
    {
        var unlinked = Ds with { BaseContractId=null };
        Assert.Equal("A-123", SqlServerKsContractScope.EffectiveNumber(
            unlinked, new[] { Main, unlinked }));
    }

    [Fact]
    public void TwoDistinctMainsDoNotGuessUnlinkedDs()
    {
        var unlinked = Ds with { BaseContractId=null };
        Assert.Equal("1", SqlServerKsContractScope.EffectiveNumber(
            unlinked, new[] { Main, Other, unlinked }));
    }

    [Fact]
    public void UnrelatedDsMustNotJoinCurrentAgreement()
    {
        var second = Ds with { Id=5, BaseContractId=3 };
        var group = SqlServerKsContractScope.ResolveDocuments(1,
            new[] { Main, Ds, Other, second });
        Assert.Equal(2,group.Count);
        Assert.DoesNotContain(group, x => x.Id == 5);
    }

    [Fact]
    public void TwoUnnumberedContractsNeverMix()
    {
        var first = Main with { Number = "" };
        var second = Other with { Number = "" };
        var result = SqlServerKsContractScope.ResolveDocuments(first.Id,
            new[] { first, second });
        Assert.Equal(first.Id, Assert.Single(result).Id);
    }

    [Fact]
    public void UnknownDocumentFailsExplicitly()
    {
        Assert.Throws<InvalidOperationException>(()=>
            SqlServerKsContractScope.ResolveDocuments(999,new[] {Main}));
    }
}
