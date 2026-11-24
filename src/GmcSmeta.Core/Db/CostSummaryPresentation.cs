using System.Globalization;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Pure read-only presentation of SQL Server cost summary figures. The projection
/// never executes commands and does not duplicate component costs already folded
/// into their owning top-level position by SqlServerCostSummary.
/// </summary>
public enum CostSummaryLineKind { System, Position, Extra, Total }

public sealed record CostSummaryGridRow
{
    public CostSummaryLineKind Kind { get; init; }
    public long PositionId { get; init; }
    public long SystemId { get; init; }
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Supplier { get; init; } = "";
    public int PositionCount { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? MatUnitCost { get; init; }
    public decimal? MatUnitSell { get; init; }
    public decimal? WorkUnitCost { get; init; }
    public decimal? WorkUnitSell { get; init; }
    public decimal MatCost { get; init; }
    public decimal MatSell { get; init; }
    public decimal WorkCost { get; init; }
    public decimal WorkSell { get; init; }
    public bool NoCost { get; init; }
    public bool NoPrice { get; init; }
    public bool NoWorkCost { get; init; }
    public bool NoWorkSell { get; init; }
    public bool IsCollapsed { get; init; }
    public bool IsSystem => Kind == CostSummaryLineKind.System;
    public bool IsPosition => Kind == CostSummaryLineKind.Position;
    public bool IsExtra => Kind == CostSummaryLineKind.Extra;
    public bool IsTotal => Kind == CostSummaryLineKind.Total;
    public bool MissingPrice => NoCost || NoPrice ||
        ((WorkUnitCost.HasValue || WorkUnitSell.HasValue) && (NoWorkCost || NoWorkSell));
    public string QtyLabel => IsSystem ? $"{PositionCount} поз." :
        Quantity?.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU")) ?? "";
    public decimal MatMargin => MatSell - MatCost;
    public decimal TotalCost => MatCost + WorkCost;
    public decimal TotalSell => MatSell + WorkSell;
    public decimal TotalMargin => TotalSell - TotalCost;
    public decimal? WorkMarkup => WorkCost == 0m ? null :
        decimal.Round(WorkMargin / WorkCost * 100m, 1);
    public decimal WorkMargin => WorkSell - WorkCost;
}

/// <summary>
/// Builds a single copy of each section, top-level position and extra expense.
/// Section headers and the grand total are aggregates, not additional detail
/// records. Filters never change the financial totals in the original data.
/// </summary>
public static class CostSummaryPresentation
{
    public static IReadOnlyList<CostSummaryGridRow> Build(CostSummaryData data)
    {
        var result = new List<CostSummaryGridRow>();
        var bySystem = data.Rows.GroupBy(r => r.SystemId).ToDictionary(g => g.Key, g => g.ToList());
        var visited = new HashSet<long>();

        void AppendSection(long id, string label)
        {
            if (!visited.Add(id)) return;
            if (!bySystem.TryGetValue(id, out var positions) || positions.Count == 0) return;
            result.Add(new CostSummaryGridRow
            {
                Kind = CostSummaryLineKind.System, SystemId = id, Name = label,
                PositionCount = positions.Count,
                MatCost = positions.Sum(r => r.MCost), MatSell = positions.Sum(r => r.MSell),
                WorkCost = positions.Sum(r => r.WCost), WorkSell = positions.Sum(r => r.WSell)
            });
            foreach (var row in positions)
                result.Add(new CostSummaryGridRow
                {
                    Kind = CostSummaryLineKind.Position, SystemId = id,
                    PositionId = row.Id, Name = row.Name, Unit = row.Unit,
                    Supplier = row.Supplier, Quantity = row.Qty,
                    MatUnitCost = row.UCost, MatUnitSell = row.Work ? null : row.UPrice,
                    WorkUnitCost = row.UWcost, WorkUnitSell = row.UWsell,
                    MatCost = row.MCost, MatSell = row.MSell, WorkCost = row.WCost, WorkSell = row.WSell,
                    NoCost = row.NoCost, NoPrice = !row.Work && row.NoPrice,
                    NoWorkCost = row.NowCost, NoWorkSell = row.NowSell
                });
        }

        foreach (var system in data.Systems)
            AppendSection(system.Id, system.IsSub ? "    " + system.Label : system.Label);
        // Defensive: still display positions if the systems metadata is incomplete.
        foreach (var group in bySystem)
            AppendSection(group.Key, $"Раздел {group.Key}");

        decimal extraMatCost = 0m, extraMatSell = 0m, extraWorkCost = 0m, extraWorkSell = 0m;
        foreach (var extra in data.Extra)
        {
            bool work = string.Equals(extra.Kind, "works", StringComparison.OrdinalIgnoreCase);
            if (work) { extraWorkCost += extra.Cost; extraWorkSell += extra.Sell; }
            else { extraMatCost += extra.Cost; extraMatSell += extra.Sell; }
            result.Add(new CostSummaryGridRow
            {
                Kind = CostSummaryLineKind.Extra, Name = extra.Name,
                MatCost = work ? 0m : extra.Cost, MatSell = work ? 0m : extra.Sell,
                WorkCost = work ? extra.Cost : 0m, WorkSell = work ? extra.Sell : 0m
            });
        }

        result.Add(new CostSummaryGridRow
        {
            Kind = CostSummaryLineKind.Total, Name = "ИТОГО",
            PositionCount = data.Rows.Count,
            MatCost = data.Rows.Sum(r => r.MCost) + extraMatCost,
            MatSell = data.Rows.Sum(r => r.MSell) + extraMatSell,
            WorkCost = data.Rows.Sum(r => r.WCost) + extraWorkCost,
            WorkSell = data.Rows.Sum(r => r.WSell) + extraWorkSell
        });
        return result;
    }

    public static IReadOnlyList<CostSummaryGridRow> Filter(
        IReadOnlyList<CostSummaryGridRow> source, long? systemId,
        string? query, bool missingOnly, IReadOnlySet<long>? collapsed = null)
    {
        var text = (query ?? "").Trim();
        bool Match(CostSummaryGridRow row) =>
            text.Length == 0 ||
            row.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            row.Supplier.Contains(text, StringComparison.OrdinalIgnoreCase);

        var result = new List<CostSummaryGridRow>();
        for (var i = 0; i < source.Count;)
        {
            var current = source[i];
            if (current.IsSystem)
            {
                var start = i + 1;
                var end = start;
                while (end < source.Count && source[end].IsPosition) end++;
                if (systemId is null || systemId == current.SystemId)
                {
                    var matched = source.Skip(start).Take(end - start)
                        .Where(row => (!missingOnly || row.MissingPrice) &&
                                      (Match(row) || (text.Length > 0 && Match(current))))
                        .ToList();
                    if (matched.Count > 0 || (text.Length == 0 && !missingOnly))
                    {
                        var folded = collapsed?.Contains(current.SystemId) == true;
                        result.Add(current with { IsCollapsed = folded });
                        if (!folded || text.Length > 0 || missingOnly)
                            result.AddRange(matched);
                    }
                }
                i = end;
                continue;
            }

            // Extras only belong to "Все системы", never to one selected section.
            if (current.IsExtra && systemId is null && !missingOnly && Match(current))
                result.Add(current);
            if (current.IsTotal && systemId is null && text.Length == 0 && !missingOnly)
                result.Add(current);
            i++;
        }
        return result;
    }
}
