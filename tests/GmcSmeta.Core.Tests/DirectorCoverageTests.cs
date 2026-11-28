using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class DirectorCoverageTests
{
    [Theory]
    [InlineData(1000, 0, 0)]
    [InlineData(1000, 250, 25)]
    [InlineData(1000, 1000, 100)]
    public void CoverageUsesFullRevenue(int revenue, int covered, int expected)
    {
        Assert.Equal((decimal?)expected, ContractFinance.CoveragePercent(revenue, covered));
    }

    [Theory]
    [InlineData(0,0,2,true)]
    [InlineData(1,2,0,false)]
    [InlineData(2,2,0,true)]
    [InlineData(1,1,1,true)]
    public void LegalScopeCountsIndependentIdsAndRetainsUnlinkedDocuments(int distinct,int linked,int missing,bool warning)
    {
        var links = Enumerable.Range(0, linked).Select(i => (long?)(distinct == 1 ? 42 : 42 + i))
            .Concat(Enumerable.Repeat<long?>(null, missing));
        var scope = DirectorLegalScope.FromLinks(links);
        Assert.Equal(distinct, scope.AgreementCount);
        Assert.Equal(linked, scope.LinkedDocumentCount);
        Assert.Equal(missing, scope.UnlinkedDocumentCount);
        Assert.Equal(warning, scope.HasWarning);
        Assert.Equal(warning, scope.LabelSuffix.Length > 0);
        Assert.Contains("Итоги по объекту", scope.Explanation);
        if (missing > 0) Assert.Contains("Их суммы также включены", scope.Explanation);
    }

    [Fact]
    public void LegalScopeUsesLongIdentityRatherThanNumber()
    {
        var scope = DirectorLegalScope.FromLinks(new long?[] { 2147483648, 2147483648, 2147483649, 0, null });
        Assert.Equal(2, scope.AgreementCount);
        Assert.Equal(3, scope.LinkedDocumentCount);
        Assert.Equal(2, scope.UnlinkedDocumentCount);
    }

    [Fact]
    public void ZeroRevenueHasNoCoverage() =>
        Assert.Null(ContractFinance.CoveragePercent(0m, 0m));

    [Fact]
    public void MultipleContractsPreserveFullAndCoveredRevenueSeparately()
    {
        var finance = ContractFinance.Sum(new[]
        {
            new ContractFinance(Cost: 200m, CoveredRevenue: 400m, MissingPrices: 1)
                { MaterialRevenue = 1000m },
            new ContractFinance(Cost: 50m, CoveredRevenue: 100m)
                { MaterialRevenue = 100m },
        });
        Assert.Equal(1100m, finance.MaterialRevenue);
        Assert.Equal(500m, finance.CoveredRevenue);
        Assert.Equal(250m, finance.Cost);
        Assert.Equal(1, finance.MissingPrices);
        Assert.Equal(500m / 1100m * 100m,
            ContractFinance.CoveragePercent(finance.MaterialRevenue, finance.CoveredRevenue)!.Value);
        Assert.Equal((decimal?)250m, finance.Earned(0m));
    }
}
