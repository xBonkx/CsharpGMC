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
    public void UnnumberedContractsAndTheirAddendaGroupWithinOneObject()
    {
        var first = Main with { Number = "" };
        var second = Other with { Number = "" };
        var foreign = first with { Id = 99, ObjectId = 20 };
        var result = SqlServerKsContractScope.ResolveDocuments(first.Id,
            new[] { first, second, Ds, foreign });
        Assert.Equal(new long[] { first.Id, Ds.Id, second.Id }, result.Select(doc => doc.Id));
    }

    [Fact]
    public void UnknownDocumentFailsExplicitly()
    {
        Assert.Throws<InvalidOperationException>(()=>
            SqlServerKsContractScope.ResolveDocuments(999,new[] {Main}));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(500, 1)]
    [InlineData(501, 2)]
    [InlineData(2500, 5)]
    public void ContractorReadBatchesAreBoundedAndPreserveAllDocumentIds(int count, int batches)
    {
        var docs = Enumerable.Range(1, count).Select(i => Main with { Id = i }).ToArray();
        var result = SqlServerKsPrintData.ContractorBatches(docs).ToArray();
        Assert.Equal(batches, result.Length);
        Assert.All(result, batch => Assert.InRange(batch.Length, 1, 500));
        Assert.Equal(docs.Select(d => d.Id), result.SelectMany(b => b).Select(d => d.Id));
    }

    [Fact]
    public void ContractorReadBatchesNeverMixObjectsOrDuplicateIds()
    {
        var docs = new[] { Main, Ds with { ObjectId = 99 }, Other };
        var batches = SqlServerKsPrintData.ContractorBatches(docs).ToArray();
        Assert.Equal(2, batches.Length);
        Assert.Equal(new long[] { Main.Id, Other.Id }, batches[0].Select(d => d.Id));
        Assert.Equal(99L, Assert.Single(batches[1]).ObjectId);
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerKsPrintData.ContractorBatches(new[] { Main, Main }));
    }

    [Fact]
    public void ContractorCoverageRejectsMissingDocumentEvenWhenOtherRequisitesExist()
    {
        var docs = new[] { Main, Ds };
        var contractors = new Dictionary<long, SqlServerKsPrintData.Contractor>
        {
            [Main.Id] = new(Main.Id, "", "", "", "", "")
        };
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerKsPrintData.RequireContractorCoverage(docs, contractors));
        contractors[Ds.Id] = new(Ds.Id, "", "", "", "", "");
        SqlServerKsPrintData.RequireContractorCoverage(docs, contractors);
    }

}
