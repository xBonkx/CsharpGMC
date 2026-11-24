using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ProcurementViewPlannerTests
{
    private static ProcurementViewSource Row(
        long id, int order, string system, string name, string unit = "шт",
        string supplier = "", string status = "none", decimal? cost = null,
        DateTime? issue = null, long systemId = 1, decimal quantity = 1) =>
        new(id, systemId, system, name, unit, supplier, status, quantity, cost, issue, order);

    [Theory]
    [InlineData("kp")]
    [InlineData("same")]
    [InlineData("sup")]
    public void EveryMode_PreservesPhysicalIds(string mode)
    {
        var rows = new[] { Row(10, 0, "П1", "Труба"), Row(11, 1, "П1", "Труба"), Row(12, 2, "В1", "Решётка", systemId: 2) };
        var projected = ProcurementViewPlanner.Build(rows, mode, "kp");
        Assert.Equal(3, projected.Count);
        Assert.Equal(new long[] { 10, 11, 12 }, projected.Select(x => x.PositionId).OrderBy(x => x));
    }

    [Fact]
    public void Kp_OriginalOrderHasSeparateSystemGroups_AndOtherSortIsFlat()
    {
        var rows = new[] { Row(10, 0, "А", "Я", systemId: 2), Row(11, 1, "Б", "А", systemId: 3) };
        var kp = ProcurementViewPlanner.Build(rows, "kp", "kp");
        Assert.NotEqual(kp[0].GroupLabel, kp[1].GroupLabel);
        Assert.Equal(new long[] { 11, 10 }, ProcurementViewPlanner.Build(rows, "kp", "name").Select(x => x.PositionId));
        Assert.All(ProcurementViewPlanner.Build(rows, "kp", "name"), x => Assert.Equal("", x.GroupLabel));
    }

    [Fact]
    public void Same_UsesNormalizedNameAndUnit_WithoutMergingDifferentUnits()
    {
        var rows = new[] { Row(1, 0, "А", "ТРУБА", "м", quantity: 2), Row(2, 1, "Б", " труба ", "М", quantity: 3), Row(3, 2, "В", "труба", "шт") };
        var result = ProcurementViewPlanner.Build(rows, "same", "kp");
        Assert.Equal(result[0].GroupLabel, result[1].GroupLabel);
        Assert.NotEqual(result[1].GroupLabel, result[2].GroupLabel);
        Assert.Contains("всего 5", result[0].GroupLabel);
    }

    [Fact]
    public void Supplier_GroupingPutsUnassignedLast_AndCountsNotOrdered()
    {
        var rows = new[] { Row(1, 0, "А", "A", supplier: ""), Row(2, 1, "Б", "B", supplier: "Цех"), Row(3, 2, "В", "C", supplier: "Цех", status: "paid") };
        var result = ProcurementViewPlanner.Build(rows, "sup", "kp");
        Assert.Equal(new long[] { 2, 3, 1 }, result.Select(x => x.PositionId));
        Assert.Contains("не заказано 1", result[0].GroupLabel);
        Assert.Contains("Поставщик не указан", result[2].GroupLabel);
    }

    [Fact]
    public void CostSorting_NullsLastInBothDirections()
    {
        var rows = new[] { Row(1, 0, "А", "A", cost: null), Row(2, 1, "А", "B", cost: 9m), Row(3, 2, "А", "C", cost: 1m) };
        Assert.Equal(new long[] { 3, 2, 1 }, ProcurementViewPlanner.Build(rows, "kp", "cost_up").Select(x => x.PositionId));
        Assert.Equal(new long[] { 2, 3, 1 }, ProcurementViewPlanner.Build(rows, "kp", "cost_down").Select(x => x.PositionId));
    }

    [Fact]
    public void IssueDate_AndStatusSort_MatchPythonPriority()
    {
        var rows = new[]
        {
            Row(1, 0, "А", "A", status: "issued", issue: null),
            Row(2, 1, "А", "B", status: "none", issue: new DateTime(2026, 11, 19)),
            Row(3, 2, "А", "C", status: "paid", issue: new DateTime(2026, 11, 20)),
        };
        Assert.Equal(new long[] { 3, 2, 1 }, ProcurementViewPlanner.Build(rows, "kp", "issued_date").Select(x => x.PositionId));
        Assert.Equal(new long[] { 2, 3, 1 }, ProcurementViewPlanner.Build(rows, "kp", "status").Select(x => x.PositionId));
    }

    [Fact]
    public void InvalidModeOrSort_ThrowsWithoutMutation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProcurementViewPlanner.Build([], "unknown", "kp"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProcurementViewPlanner.Build([], "kp", "unknown"));
    }
}
