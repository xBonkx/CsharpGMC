using System.Globalization;

namespace GmcSmeta.Core.Domain;

/// <summary>Строка предпросмотра сравнения — цвет фона по типу (compare_view LOOK).</summary>
public sealed class ComparePreviewRow
{
    public string Sys { get; init; } = "";
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Qty { get; init; } = "";
    public string Price { get; init; } = "";
    public string SumA { get; init; } = "";
    public string SumB { get; init; } = "";
    public string Diff { get; init; } = "";
    public string RowBackground { get; init; } = "White";
}

/// <summary>Preformatted comparison views; shared rows and no WPF objects on the worker.</summary>
public sealed record CompareDisplaySnapshot(
    IReadOnlyList<ComparePreviewRow> AllRows, IReadOnlyList<ComparePreviewRow> ChangedRows,
    string Materials, string Smr, string Total, string Count, string Info);

public static class CompareDisplayProjection
{
    public static CompareDisplaySnapshot Build(CompareResult result, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var all = new List<ComparePreviewRow>(result.Rows.Count);
        var changed = new List<ComparePreviewRow>();
        foreach (var r in result.Rows)
        {
            token.ThrowIfCancellationRequested();
            var row = new ComparePreviewRow
            {
                Sys = r.Sys,
                Name = r.Changed.Contains("sys") ? $"{r.Name}   (было в {r.SysWas})" : r.Name,
                Unit = r.Unit,
                Qty = Arrow(r.QtyA, r.QtyB, QtyText),
                Price = Arrow(r.PriceA, r.PriceB, Money.Format),
                SumA = r.QtyA is null ? "" : Money.Format(r.SumA),
                SumB = r.QtyB is null ? "" : Money.Format(r.SumB),
                Diff = Signed(r.Diff),
                RowBackground = r.Kind switch
                {
                    "added" => "#ECFDF3",
                    "removed" => "#FEF2F2",
                    "changed" => "#FFFBEB",
                    _ => "White",
                },
            };
            all.Add(row);
            if (r.Kind != "same") changed.Add(row);
        }
        var empty = new ComparePreviewRow
        {
            Name = "Отличий нет — документы совпадают по строкам, количествам и ценам.",
        };
        if (all.Count == 0) all.Add(empty);
        if (changed.Count == 0) changed.Add(empty);
        var d = result.TotalB - result.TotalA;
        token.ThrowIfCancellationRequested();
        return new CompareDisplaySnapshot(all.AsReadOnly(), changed.AsReadOnly(),
            Money.Format(result.MatB) + Delta(result.MatB - result.MatA),
            Money.Format(result.SmrB) + Delta(result.SmrB - result.SmrA),
            Money.Format(result.TotalB) + Delta(d),
            $"+{result.Added}  −{result.Removed}  ~{result.ChangedCount}",
            $"Итого было {Money.Format(result.TotalA)} ₽, стало {Money.Format(result.TotalB)} ₽" +
                (Math.Abs(d) >= 0.005m ? $"  ({Signed(d)} ₽)" : "  (без изменений)"));
    }

    private static string Arrow(decimal? a, decimal? b, Func<decimal, string> fmt)
    {
        if (a is null) return fmt(b!.Value);
        if (b is null) return fmt(a.Value);
        var before = fmt(a.Value);
        var after = fmt(b.Value);
        return before == after ? after : $"{before} → {after}";
    }

    private static string Signed(decimal v) => Math.Abs(v) < 0.005m ? ""
        : (v > 0 ? "+" : "−") + Money.Format(Math.Abs(v));

    private static string QtyText(decimal v) => v == decimal.Truncate(v)
        ? v.ToString("0", CultureInfo.CurrentCulture)
        : v.ToString("0.###", CultureInfo.CurrentCulture);

    private static string Delta(decimal d) => Math.Abs(d) < 0.005m ? "" : "   " + Signed(d);
}
