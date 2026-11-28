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
    [Fact]
    public void DisplayProjectionSharesRowsAndPreservesFilterAndFormatting()
    {
        var a = Doc("КП 1", Sys(1, "В1"), Pos(10, 1, "Решётка", "шт", 10m, 100m),
            Pos(11, 1, "Без изменений", "шт", 1m, 10m), Pos(12, 1, "Убрана", "шт", 1m, 5m));
        var b = Doc("КП 2", Sys(1, "В1"), Sys(2, "В2"),
            Pos(20, 2, "Решётка", "шт", 12m, 110m),
            Pos(21, 1, "Без изменений", "шт", 1m, 10m), Pos(22, 1, "Добавлена", "шт", 2m, 6m));
        var result = DocumentCompare.CompareDocuments(a, b);
        var display = CompareDisplayProjection.Build(result);
        Assert.Equal(4, display.AllRows.Count);
        Assert.Equal(3, display.ChangedRows.Count);
        Assert.DoesNotContain(display.ChangedRows, row => row.Name == "Без изменений");
        var changed = Assert.Single(display.ChangedRows.Where(row => row.Name.Contains("Решётка")));
        Assert.Same(changed, Assert.Single(display.AllRows.Where(row => row.Name.Contains("Решётка"))));
        Assert.Equal("Решётка   (было в В1)", changed.Name);
        Assert.Equal("10 → 12", changed.Qty);
        Assert.Equal("#FFFBEB", changed.RowBackground);
        Assert.Equal("", Assert.Single(display.ChangedRows.Where(row => row.Name == "Добавлена")).SumA);
        Assert.Equal("", Assert.Single(display.ChangedRows.Where(row => row.Name == "Убрана")).SumB);
        Assert.Equal("+1  −1  ~1", display.Count);
    }

    [Fact]
    public void DisplayProjectionShowsPlaceholderOnlyInEmptyViewsAndHonorsCancellation()
    {
        var doc = Doc("КП", Sys(1, "В"), Pos(10, 1, "Решётка", "шт", 1m, 1m));
        var result = DocumentCompare.CompareDocuments(doc, doc);
        var display = CompareDisplayProjection.Build(result);
        Assert.Equal("Решётка", Assert.Single(display.AllRows).Name);
        Assert.StartsWith("Отличий нет", Assert.Single(display.ChangedRows).Name);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CompareDisplayProjection.Build(result, cancellation.Token));
    }

    [Fact]
    public void NormalizedSystemNamesKeepOriginalGroupingAndMoveSemantics()
    {
        var a = Doc("A", Sys(1, "  ВЕНТИЛЯЦИЯ   П1 "), Sys(2, "Старая"),
            Pos(10, 1, "Сохранена", "шт.", 1m, 10m),
            Pos(11, 1, "Убрана", "шт", 1m, 10m),
            Pos(12, 2, "Перенесена", "шт", 2m, 5m),
            Pos(13, 2, "Только старая", "шт", 1m, 1m));
        var b = Doc("B", Sys(3, "Новая"), Sys(4, "вентиляция п1"),
            Pos(20, 3, "Перенесена", "шт", 2m, 5m),
            Pos(21, 4, "Сохранена", "шт", 1m, 10m),
            Pos(22, 4, "Добавлена", "шт", 3m, 7m));
        var result = DocumentCompare.CompareDocuments(a, b);
        Assert.Equal(new[] { "Перенесена", "Сохранена", "Добавлена", "Убрана", "Только старая" },
            result.Rows.Select(r => r.Name).ToArray());
        Assert.Equal(new[] { "sys" }, result.Rows[0].Changed);
        Assert.Equal("same", result.Rows[1].Kind);
        Assert.Equal("  ВЕНТИЛЯЦИЯ   П1 ", result.Rows[3].Sys);
        Assert.Equal(1, result.Same);
        Assert.Equal(1, result.ChangedCount);
        Assert.Equal(1, result.Added);
        Assert.Equal(2, result.Removed);
    }

    [Theory]
    [InlineData(2500)]
    [InlineData(10000)]
    public void LargeComparisonPreservesDuplicatePairingAndCounts(int count)
    {
        var a = new[] { Sys(1, "П1") }.Concat(Enumerable.Range(0, count)
            .Select(i => Pos(i + 10, 1, "Решётка", "шт.", i + 1m, 2m))).ToArray();
        var b = new[] { Sys(2, "  п1 ") }.Concat(Enumerable.Range(0, count)
            .Select(i => Pos(i + 10, 2, "решётка", "шт", i + 2m, 2m))).ToArray();
        var result = DocumentCompare.CompareDocuments(Doc("A", a), Doc("B", b));
        Assert.Equal(count, result.ChangedCount);
        Assert.Equal(count, result.Rows.Count);
        Assert.Equal(0, result.Added + result.Removed + result.Same);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(i + 1m, result.Rows[i].QtyA);
            Assert.Equal(i + 2m, result.Rows[i].QtyB);
            Assert.Equal(2m, result.Rows[i].Diff);
            Assert.Equal(new[] { "qty" }, result.Rows[i].Changed);
        }
    }

    [Fact]
    public void CancelledComparisonDoesNotBuildRows()
    {
        var doc = Doc("КП", Sys(1, "П1"), Pos(10, 1, "Решётка", "шт", 1m, 1m));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            DocumentCompare.CompareDocuments(doc, doc, cancellation.Token));
    }


    [Fact]
    public void LargeMixedComparisonPreservesExactFirstPassPriorityAndSecondPassPairing()
    {
        const int count = 25000;
        var a = new[] { Sys(1, "A"), Sys(2, "B") }.Concat(Enumerable.Range(0, count)
            .Select(i => Pos(i + 10, i % 2 + 1, "Позиция " + (i / 2), "шт", 1m, 2m))).ToArray();
        // Keep even rows in A; move odd rows from B to C. Add a new unique row.
        var b = new[] { Sys(3, "A"), Sys(4, "C") }.Concat(Enumerable.Range(0, count)
            .Where(i => i != count - 1)
            .Select(i => Pos(i + 10, i % 2 + 3, "Позиция " + (i / 2), "шт", 1m, 2m)))
            .Append(Pos(count + 10, 4, "Новая", "шт", 3m, 2m)).ToArray();
        var result = DocumentCompare.CompareDocuments(Doc("A", a), Doc("B", b));
        Assert.Equal(count / 2, result.Same);
        Assert.Equal(count / 2 - 1, result.ChangedCount);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
        Assert.Equal(count + 1, result.Rows.Count);
        Assert.All(result.Rows.Where(r => r.Kind == "changed"), r =>
        {
            Assert.Equal(new[] { "sys" }, r.Changed);
            Assert.Equal("B", r.SysWas);
            Assert.Equal("C", r.Sys);
        });
    }
}
