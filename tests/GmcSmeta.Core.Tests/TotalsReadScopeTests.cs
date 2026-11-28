using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

/// <summary>Contract-only totals must never read unrelated contracts/positions.</summary>
public sealed class TotalsReadScopeTests
{
    [Fact]
    public void ScopedQueries_AllFourQueriesRequireContractParameter()
    {
        var q = Totals.ScopedQueries();
        var statements = new[] { q.Contracts, q.Systems, q.Positions, q.Counts };
        Assert.All(statements, sql =>
        {
            Assert.Contains("@id", sql);
            Assert.Contains("WHERE ", sql, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ScopedQueries_PositionScanIsFilteredByJoinedSystemContract()
    {
        var q = Totals.ScopedQueries();
        Assert.Contains("JOIN systems sy ON sy.id=p.system_id", q.Positions);
        Assert.Contains("sy.contract_id=@id", q.Positions);
        Assert.Contains("p.parent_id IS NULL", q.Positions);
        Assert.Contains("sy.contract_id=@id", q.Counts);
        Assert.DoesNotContain("SELECT *", q.Positions);
    }

    [Fact]
    public void ScopedQueries_OnlyContractScopedSystemsAndContractHeader()
    {
        var q = Totals.ScopedQueries();
        Assert.Contains("FROM contracts WHERE id=@id", q.Contracts);
        Assert.Contains("FROM systems WHERE contract_id=@id", q.Systems);
    }

    [Fact]
    public void EmptyDirectorSelectionDoesNotReadDatabase()
    {
        var result = FinanceReader.ReadSelected(null!, Array.Empty<int>(), out var totals);
        Assert.Empty(result);
        Assert.Empty(totals);
    }

    [Fact]
    public void InvalidDirectorSelectionFailsBeforeConnecting()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FinanceReader.ReadSelected(null!, new[] { 1, -1 }, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReadOne_InvalidIdDoesNotOpenOrReadConnection(int id)
    {
        Assert.Null(Totals.ReadOne(null!, id));
    }

    [Fact]
    public void EmptyStageTotalsSelectionDoesNotReadDatabase()
    {
        Assert.Empty(Totals.ReadSelected(null!, Array.Empty<int>()));
    }

    [Fact]
    public void InvalidStageTotalsSelectionFailsBeforeDatabaseRead()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Totals.ReadSelected(null!, new[] { 1, 0 }));
    }

    [Fact]
    public void CancelledStageTotalsSelectionFailsBeforeDatabaseRead()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            Totals.ReadSelected(null!, new[] { 1 }, cancellation.Token));
    }
}
