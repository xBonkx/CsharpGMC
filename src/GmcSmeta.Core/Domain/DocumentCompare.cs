namespace GmcSmeta.Core.Domain;

/// <summary>Строка сравнения двух документов (compare.py): same/added/removed/changed.</summary>
public sealed record CompareRow(
    string Kind, string[] Changed, string Sys, string SysWas, string Name, string Unit,
    decimal? QtyA, decimal? QtyB, decimal? PriceA, decimal? PriceB, decimal SumA, decimal SumB,
    decimal Diff, int Order, bool IsSystemRow = false);

/// <summary>Результат сравнения: строки, счётчики, итоги и названия (compare.compare).</summary>
public sealed record CompareResult(
    IReadOnlyList<CompareRow> Rows,
    int Added, int Removed, int ChangedCount, int Same,
    decimal MatA, decimal MatB, decimal SmrA, decimal SmrB, decimal TotalA, decimal TotalB,
    string TitleA, string TitleB);

/// <summary>
/// Сравнение двух документов (КП-вариантов, КП и договора): что добавлено, убрано,
/// изменено. Строки сопоставляются по разделу + имени + единице; строка, переехавшая
/// в другой раздел, всё равно находится (по имени и единице); одинаковые имена в одном
/// разделе — по порядку следования.
/// </summary>
public static class DocumentCompare
{
    private static string Norm(string? text) =>
        string.Join(' ', (text ?? "").Replace('ё', 'е').ToLowerInvariant().Split(' ',
            StringSplitOptions.RemoveEmptyEntries));

    private static string Unit(string? u) => Norm(u).TrimEnd('.');

    private sealed record SrcRow(string Sys, string Name, string Unit, decimal Qty, decimal Price,
        decimal Sum, (string, string, string) KSys, (string, string) KAny, int Order);

    private static List<SrcRow> RowsOf(EstimateDocument doc, CancellationToken token)
    {
        var result = new List<SrcRow>();
        var systemNames = doc.Rows.Where(r => r.IsSystem)
            .ToDictionary(r => r.SystemId, r => (Name: r.Name, Key: Norm(r.Name)));
        foreach (var row in doc.Rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.IsSystem || row.ParentPositionId is not null || row.OutOfKp) continue;
            var system = systemNames.GetValueOrDefault(row.SystemId, (Name: "", Key: ""));
            var nameKey = Norm(row.Name);
            var unitKey = Unit(row.Unit);
            result.Add(new SrcRow(system.Name, row.Name, row.Unit, row.Qty, row.Price, row.Amount,
                (system.Key, nameKey, unitKey), (nameKey, unitKey), result.Count));
        }
        return result;
    }

    /// <summary>a = более старый/базовый документ, b = новый.</summary>
    public static CompareResult CompareDocuments(EstimateDocument a, EstimateDocument b,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var ra = RowsOf(a, token);
        var rb = RowsOf(b, token);
        var freeB = new Dictionary<(string, string, string), Queue<int>>();
        for (var j = 0; j < rb.Count; j++)
        {
            token.ThrowIfCancellationRequested();
            Add(freeB, rb[j].KSys, j);
        }
        var pair = new Dictionary<int, int>();
        foreach (var r in ra)                                    // 1) тот же раздел, имя и единица
        {
            token.ThrowIfCancellationRequested();
            if (freeB.TryGetValue(r.KSys, out var queue) && queue.Count > 0)
                pair[r.Order] = queue.Dequeue();
        }
        var used = pair.Values.ToHashSet();
        var byAny = new Dictionary<(string, string), Queue<int>>();
        for (var j = 0; j < rb.Count; j++)
        {
            token.ThrowIfCancellationRequested();
            if (!used.Contains(j))
                Add(byAny, rb[j].KAny, j);
        }
        foreach (var r in ra)                                    // 2) переехал в другой раздел
        {
            token.ThrowIfCancellationRequested();
            if (pair.ContainsKey(r.Order)) continue;
            if (byAny.TryGetValue(r.KAny, out var any) && any.Count > 0)
                pair[r.Order] = any.Dequeue();
        }
        var usedFinal = pair.Values.ToHashSet();

        var rows = new List<CompareRow>();
        foreach (var r in ra)
        {
            token.ThrowIfCancellationRequested();
            if (pair.TryGetValue(r.Order, out var j))
            {
                var n = rb[j];
                var changed = new List<string>();
                if (Math.Abs(r.Qty - n.Qty) > 0.000000001m) changed.Add("qty");
                if (Math.Abs(r.Price - n.Price) > 0.005m) changed.Add("price");
                if (r.KSys.Item1 != n.KSys.Item1) changed.Add("sys");
                rows.Add(new CompareRow(changed.Count > 0 ? "changed" : "same", changed.ToArray(),
                    n.Sys, r.Sys, n.Name, n.Unit, r.Qty, n.Qty, r.Price, n.Price,
                    r.Sum, n.Sum, decimal.Round(n.Sum - r.Sum, 2), j));
            }
            else
            {
                rows.Add(new CompareRow("removed", Array.Empty<string>(), r.Sys, r.Sys, r.Name, r.Unit,
                    r.Qty, null, r.Price, null, r.Sum, 0m, decimal.Round(-r.Sum, 2), -1));
            }
        }
        for (var j = 0; j < rb.Count; j++)
        {
            token.ThrowIfCancellationRequested();
            if (usedFinal.Contains(j)) continue;
            var n = rb[j];
            rows.Add(new CompareRow("added", Array.Empty<string>(), n.Sys, "", n.Name, n.Unit,
                null, n.Qty, null, n.Price, 0m, n.Sum, decimal.Round(n.Sum, 2), j));
        }
        // Группировка по разделам в порядке нового документа; убранные — в конец своего раздела.
        var sysOrder = new Dictionary<string, int>();
        var rawOrder = new Dictionary<string, int>();
        void RegisterSystems(List<SrcRow> source)
        {
            foreach (var r in source)
            {
                token.ThrowIfCancellationRequested();
                var key = r.KSys.Item1;
                if (!sysOrder.TryGetValue(key, out var index))
                {
                    index = sysOrder.Count;
                    sysOrder.Add(key, index);
                }
                rawOrder.TryAdd(r.Sys, index);
            }
        }
        RegisterSystems(rb);
        RegisterSystems(ra);
        token.ThrowIfCancellationRequested();
        // Keep the original comparator, including equal-key ordering, but avoid
        // allocating normalized strings on every O(n log n) comparison.
        rows.Sort((x, y) =>
        {
            var s = rawOrder.GetValueOrDefault(x.Sys, 999) - rawOrder.GetValueOrDefault(y.Sys, 999);
            if (s != 0) return s;
            return (x.Order < 0 ? 1_000_000 : x.Order) - (y.Order < 0 ? 1_000_000 : y.Order);
        });
        token.ThrowIfCancellationRequested();
        var added = rows.Count(r => r.Kind == "added");
        var removed = rows.Count(r => r.Kind == "removed");
        var changedCount = rows.Count(r => r.Kind == "changed");
        var same = rows.Count(r => r.Kind == "same");
        return new CompareResult(rows, added, removed, changedCount, same,
            a.Totals.Materials, b.Totals.Materials, a.Totals.Smr, b.Totals.Smr,
            a.Totals.Total, b.Totals.Total, a.Title, b.Title);
    }

    private static void Add<TKey, TValue>(Dictionary<TKey, Queue<TValue>> map, TKey key, TValue value)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var queue))
            map[key] = queue = new Queue<TValue>();
        queue.Enqueue(value);
    }
}
