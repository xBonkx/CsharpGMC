using System.Collections.Immutable;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

/// <summary>Логика сравнения двух документов — compare.py (без подключения к БД).</summary>
public sealed class DocumentCompareTests
{
    private static EstimateRow Pos(int id, int systemId, string name, string unit,
        decimal qty, decimal price, bool outOfKp = false) =>
        new(RowKind.Position, 1, id, id, false, "", name, unit, qty, price,
            qty * price, null, outOfKp, "")
        { SystemId = systemId };

    private static EstimateRow Sys(int id, string name) =>
        new(RowKind.System, 0, 0, 0, false, "", name, "", 0m, 0m, 0m, null, false, "")
        { SystemId = id };

    private static EstimateDocument Doc(string title, params EstimateRow[] rows)
    {
        // Итоги-заглушки считаются по строкам: материалы и СМР — как material_sum()/smr() эталона.
        var mat = rows.Where(r => !r.IsSystem && r.ParentPositionId is null && !r.OutOfKp &&
            !EstimateStatistics.IsWorkName(r.Name)).Sum(r => r.Qty * r.Price);
        var smr = rows.Where(r => !r.IsSystem && r.ParentPositionId is null && !r.OutOfKp &&
            EstimateStatistics.IsWorkName(r.Name)).Sum(r => r.Qty * r.Price);
        return new EstimateDocument(1, title, "", "", "", "", 22m, false, false, "", 0, 0,
            rows.ToImmutableList(), rows.ToImmutableList(),
            new ContractTotals(1, "kp", "", 22m, mat, smr, mat + smr, mat + smr, 0m, rows.Length))
        { Statistics = new EstimateStatistics(mat, smr, 0) };
    }

    [Fact]
    public void SameRows_AreMatched_AndTotalsTakenFromDocs()
    {
        var a = Doc("КП 1", Sys(1, "Вентиляция"), Pos(10, 1, "Решётка", "шт", 10m, 100m));
        var b = Doc("КП 2", Sys(1, "Вентиляция"), Pos(20, 1, "Решётка", "шт", 10m, 100m));
        var result = DocumentCompare.CompareDocuments(a, b);
        Assert.Equal(1, result.Same);
        Assert.Equal(0, result.Added);
        Assert.Equal(1000m, result.MatA);
        Assert.Equal(1000m, result.MatB);
    }

    [Fact]
    public void QtyAndPriceChanges_AreReported()
    {
        var a = Doc("КП 1", Sys(1, "В"), Pos(10, 1, "Решётка", "шт", 10m, 100m));
        var b = Doc("КП 2", Sys(1, "В"), Pos(20, 1, "Решётка", "шт", 12m, 110m));
        var result = DocumentCompare.CompareDocuments(a, b);
        Assert.Equal(1, result.ChangedCount);
        var row = Assert.Single(result.Rows);
        Assert.Equal(new[] { "qty", "price" }, row.Changed);
        Assert.Equal(1320m - 1000m, row.Diff);
    }

    [Fact]
    public void AddedAndRemoved_AreCounted()
    {
        var a = Doc("КП 1", Sys(1, "В"), Pos(10, 1, "Старая", "шт", 1m, 10m));
        var b = Doc("КП 2", Sys(1, "В"), Pos(20, 1, "Новая", "шт", 2m, 20m));
        var result = DocumentCompare.CompareDocuments(a, b);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.Same);
    }

    [Fact]
    public void MovedToAnotherSection_IsStillMatched_AsChangedSys()
    {
        var a = Doc("КП 1", Sys(1, "В1"), Pos(10, 1, "Решётка", "шт", 5m, 50m));
        var b = Doc("КП 2", Sys(1, "В1"), Sys(2, "В2"), Pos(20, 2, "Решётка", "шт", 5m, 50m));
        var result = DocumentCompare.CompareDocuments(a, b);
        Assert.Equal(1, result.ChangedCount);          // переехал в другой раздел — «изменено»
        Assert.DoesNotContain(result.Rows, r => r.Kind == "added");
        Assert.DoesNotContain(result.Rows, r => r.Kind == "removed");
        Assert.Contains("sys", Assert.Single(result.Rows).Changed);
    }

    [Fact]
    public void UnitDotInsensitive_AndCaseInsensitive()
    {
        var a = Doc("КП 1", Sys(1, "В"), Pos(10, 1, "решётка", "шт", 1m, 1m));
        var b = Doc("КП 2", Sys(1, "В"), Pos(20, 1, "Решётка", "шт.", 1m, 1m));
        var result = DocumentCompare.CompareDocuments(a, b);
        Assert.Equal(1, result.Same);
    }

    [Fact]
    public void OutOfKpRows_AreIgnored()
    {
        var a = Doc("КП 1", Sys(1, "В"), Pos(10, 1, "Решётка", "шт", 1m, 1m),
            Pos(11, 1, "Докупка вне сметы", "шт", 9m, 9m, outOfKp: true));
        var b = Doc("КП 2", Sys(1, "В"), Pos(20, 1, "Решётка", "шт", 1m, 1m));
        var result = DocumentCompare.CompareDocuments(a, b);
        Assert.Equal(1, result.Same);
        Assert.Equal(0, result.Added);
    }

    [Fact]
    public void DuplicateNames_PairedInOrderOfAppearance()
    {
        var a = Doc("КП 1", Sys(1, "В"), Pos(10, 1, "Решётка", "шт", 1m, 1m),
            Pos(11, 1, "Решётка", "шт", 2m, 2m));
        var b = Doc("КП 2", Sys(1, "В"), Pos(20, 1, "Решётка", "шт", 3m, 3m),
            Pos(21, 1, "Решётка", "шт", 4m, 4m));
        var result = DocumentCompare.CompareDocuments(a, b);
        // Пары по порядку: 1↔3, 2↔4; количества разные — «изменено».
        Assert.Equal(2, result.ChangedCount);
        Assert.Equal(0, result.Same);
        Assert.Equal(1m, result.Rows[0].QtyA);         // первая пара: 1↔3
    }

    [Fact]
    public void RemovedRow_GoesToEndOfItsSystem()
    {
        var a = Doc("КП 1", Sys(1, "В"), Pos(10, 1, "Убираем", "шт", 1m, 10m),
            Pos(11, 1, "Остаётся", "шт", 2m, 20m));
        var b = Doc("КП 2", Sys(1, "В"), Pos(20, 1, "Остаётся", "шт", 2m, 20m));
        var result = DocumentCompare.CompareDocuments(a, b);
        var removed = Assert.Single(result.Rows.Where(r => r.Kind == "removed"));
        var kept = Assert.Single(result.Rows.Where(r => r.Kind == "same"));
        // Убранная строка — в конце своего раздела.
        Assert.True(result.Rows.ToList().IndexOf(kept) < result.Rows.ToList().IndexOf(removed));
        Assert.Equal("Убираем", removed.Name);
        Assert.Equal(-10m, removed.Diff);
        Assert.Equal(40m, kept.SumB);
    }
}
