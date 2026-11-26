using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class StageAllocationPresentationTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("", 0)]
    [InlineData("2,5", 2.5)]
    [InlineData("=2*3", 6)]
    [InlineData("0,000001", 0.000001)]
    public void QuantityParsesPythonNumericSyntax(string text, double expected)
    {
        Assert.True(StageAllocationPresentation.TryParseNonnegative(text, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-0,00001")]
    [InlineData("2/0")]
    [InlineData("abc")]
    [InlineData("10^2")]
    public void InvalidQuantityCannotSilentlyBecomeZero(string text) =>
        Assert.False(StageAllocationPresentation.TryParseNonnegative(text, out _));

    [Fact]
    public void FiltersMatchPythonStageMode()
    {
        decimal[] none = [0m, 0m];
        decimal[] some = [1m, 2m];
        Assert.True(StageAllocationPresentation.Matches(0m, some, StageAllocationPresentation.All));
        Assert.True(StageAllocationPresentation.Matches(1m, none, StageAllocationPresentation.Undistributed));
        Assert.True(StageAllocationPresentation.Matches(-1m, some, StageAllocationPresentation.Undistributed));
        Assert.False(StageAllocationPresentation.Matches(0m, none, StageAllocationPresentation.Undistributed));
        Assert.True(StageAllocationPresentation.Matches(0m, some, StageAllocationPresentation.Assigned));
        Assert.False(StageAllocationPresentation.Matches(1m, none, StageAllocationPresentation.Assigned));
        Assert.True(StageAllocationPresentation.Matches(-1m, some, StageAllocationPresentation.Overrun));
        Assert.False(StageAllocationPresentation.Matches(0m, some, StageAllocationPresentation.Overrun));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StageAllocationPresentation.Matches(0m, none, "sql"));
    }

    [Fact]
    public void OverrunIsComparedWithAvailableIgnoringOwnPreviousQuantity()
    {
        decimal[] stages = [2m, 3m, 0m];
        Assert.False(StageAllocationPresentation.NeedsOverrunConfirmation(10m, stages, 1, 8m));
        Assert.True(StageAllocationPresentation.NeedsOverrunConfirmation(10m, stages, 1, 8.00001m));
        Assert.Equal(5m, StageAllocationPresentation.Remaining(10m, stages));
    }

    [Fact]
    public void RemainingDisplayShowsCheckOnlyAtZero()
    {
        Assert.Equal("✓", StageAllocationPresentation.RemainingText(0m));
        Assert.Equal("✓", StageAllocationPresentation.RemainingText(0.0000001m));
        Assert.Equal("-1", StageAllocationPresentation.RemainingText(-1m));
        Assert.Equal("1,5", StageAllocationPresentation.RemainingText(1.5m));
    }

    [Fact]
    public void UnassignedAmountIsCalculatedFromPositiveRestOnly()
    {
        var rows = new[] { (Price: 10m, Remaining: 2m),
            (Price: 50m, Remaining: -3m), (Price: 25m, Remaining: 0.5m) };
        Assert.Equal(32.5m, StageAllocationPresentation.AmountOfRemaining(rows));
    }

    [Fact]
    public void CellLabelsPreserveRemainderAndRussianQuantityFormatting()
    {
        Assert.Equal(new[] { "", "1,25", "-2", "✓" },
            StageAllocationPresentation.CellLabels(new decimal[] { 0m, 1.25m, -2m }, 0m));
        Assert.Equal(new[] { "-0,5" }, StageAllocationPresentation.CellLabels(Array.Empty<decimal>(), -0.5m));
    }

    [Fact]
    public void LargeCardMetricsMatchIndependentFormerScans()
    {
        var rows = Enumerable.Range(0, 25000).Select(i =>
            (Price: (decimal)(i % 17), Rest: i % 7 - 3m,
             PerStage: (IReadOnlyList<decimal>)new decimal[] { i % 3, i % 5 - 2, 0.000001m })).ToArray();
        var metrics = StageAllocationPresentation.Summarize(3, rows);
        Assert.Equal(Enumerable.Range(0, 3).Select(i =>
            rows.Count(r => r.PerStage[i] > StageAllocationPresentation.Epsilon)), metrics.AssignedCounts);
        Assert.Equal(rows.Count(r => Math.Abs(r.Rest) > StageAllocationPresentation.Epsilon), metrics.UnassignedCount);
        Assert.Equal(rows.Count(r => r.Rest < -StageAllocationPresentation.Epsilon), metrics.OverrunCount);
        Assert.Equal(StageAllocationPresentation.AmountOfRemaining(rows.Select(r => (r.Price, r.Rest))),
            metrics.RemainingAmount);
        var empty = StageAllocationPresentation.Summarize(0, Array.Empty<(decimal, decimal, IReadOnlyList<decimal>)>());
        Assert.Empty(empty.AssignedCounts);
        Assert.Equal(0m, empty.RemainingAmount);
    }
}
