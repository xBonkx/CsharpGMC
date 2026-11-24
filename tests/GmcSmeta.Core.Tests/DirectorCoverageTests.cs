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
