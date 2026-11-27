namespace GmcSmeta.Core.Domain;

/// <summary>Immutable data needed for temporary procurement views; never changes SQL position order.</summary>
public sealed record ProcurementViewSource(
    long PositionId, long SystemId, string SystemName, string Name, string Unit,
    string Supplier, string Status, decimal Quantity, decimal? CostPrice,
    DateTime? LastIssued, int OriginalIndex);

public sealed record ProcurementViewEntry(long PositionId, string GroupLabel);

/// <summary>
/// Pure projection of proc_view.py VIEW_MODES/VIEW_SORTS. These are temporary
/// read-only views; all writes still target the original SQL position ID.
/// </summary>
public static class ProcurementViewPlanner
{
    public static readonly (string Code, string Label)[] Modes =
    [
        ("kp", "Как в КП (по системам)"),
        ("same", "Одинаковые вместе"),
        ("sup", "По поставщику"),
    ];
    public static readonly (string Code, string Label)[] Sorts =
    [
        ("kp", "Как в КП"),
        ("name", "По названию"),
        ("issued_date", "По дате выдачи — последние сверху"),
        ("cost_up", "По закупу — дешёвые сверху"),
        ("cost_down", "По закупу — дорогие сверху"),
        ("supplier", "По поставщику"),
        ("status", "По статусу"),
    ];
    private static readonly string[] StatusOrder =
    [
        "none", "skip", "zero", "fact", "ordered", "shop_queued",
        "shop_work", "invoice", "paid", "part_stock", "stock",
        "part_issued", "issued",
    ];

    private static readonly IReadOnlyDictionary<string, int> StatusRanks =
        StatusOrder.Select((status, rank) => (status, rank))
            .ToDictionary(x => x.status, x => x.rank, StringComparer.Ordinal);

    public static IReadOnlyList<ProcurementViewEntry> Build(
        IReadOnlyList<ProcurementViewSource> rows, string mode, string sort)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (!Modes.Any(x => x.Code == mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!Sorts.Any(x => x.Code == sort)) throw new ArgumentOutOfRangeException(nameof(sort));
        // The WPF snapshot and its filtered subsets normally already have
        // original order. Keep the public API safe for arbitrary input.
        IReadOnlyList<ProcurementViewSource> all = rows;
        for (var i = 1; i < rows.Count; i++)
        {
            var previous = rows[i - 1];
            var current = rows[i];
            if (previous.OriginalIndex > current.OriginalIndex ||
                (previous.OriginalIndex == current.OriginalIndex && previous.PositionId > current.PositionId))
            {
                all = rows.OrderBy(x => x.OriginalIndex).ThenBy(x => x.PositionId).ToArray();
                break;
            }
        }

        IEnumerable<ProcurementViewSource> Sort(IEnumerable<ProcurementViewSource> source)
        {
            return sort switch
            {
                "name" => source.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.OriginalIndex),
                "issued_date" => source.OrderBy(x => x.LastIssued is null)
                    .ThenByDescending(x => x.LastIssued)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.OriginalIndex),
                "cost_up" => source.OrderBy(x => x.CostPrice is null)
                    .ThenBy(x => x.CostPrice)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.OriginalIndex),
                "cost_down" => source.OrderBy(x => x.CostPrice is null)
                    .ThenByDescending(x => x.CostPrice)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.OriginalIndex),
                "supplier" => source.OrderBy(x => string.IsNullOrWhiteSpace(x.Supplier))
                    .ThenBy(x => x.Supplier, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.OriginalIndex),
                "status" => source.OrderBy(x =>
                        x.Status is not null && StatusRanks.TryGetValue(x.Status, out var rank) ? rank : 99)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.OriginalIndex),
                // Every source is either all or a stable subsequence of all.
                _ => source,
            };
        }

        // In Python, a selected sort on "Как в КП" produces a flat list,
        // while the original KP order shows the actual systems.
        if (mode == "kp" && sort != "kp")
            return Sort(all).Select(x => new ProcurementViewEntry(x.PositionId, "")).ToList();

        if (mode == "kp")
        {
            var systemGroups = all.GroupBy(x => x.SystemId).ToArray();
            var duplicateNames = systemGroups
                .Select(g => (g.Key, g.First().SystemName))
                .GroupBy(x => x.SystemName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return systemGroups
                .SelectMany(g =>
                {
                    var title = g.First().SystemName;
                    if (duplicateNames.Contains(title)) title += " · #" + g.Key;
                    title += " · " + g.Count() + " поз.";
                    return g.Select(x => new ProcurementViewEntry(x.PositionId, title));
                }).ToList();
        }

        if (mode == "sup")
        {
            var groups = all.GroupBy(x => (x.Supplier ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key.Length == 0)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
            return groups.SelectMany(g =>
            {
                var missing = g.Count(x => x.Status == "none");
                var title = (g.Key.Length == 0 ? "Поставщик не указан" : g.Key)
                    + " · " + g.Count() + " поз."
                    + (missing > 0 ? ", не заказано " + missing : "");
                return Sort(g).Select(x => new ProcurementViewEntry(x.PositionId, title));
            }).ToList();
        }

        // Identical purchase rows are grouped by normalized name AND unit.
        // There is no write through the synthetic group header; selected
        // physical positions are updated together by SetProc in one transaction.
        return Sort(all).GroupBy(x =>
                (x.Name.Trim().ToLowerInvariant(), x.Unit.Trim().ToLowerInvariant()))
            .SelectMany(g =>
            {
                var first = g.First();
                var title = first.Name.Trim() + " · " + first.Unit.Trim()
                    + " · " + g.Count() + " поз., всего "
                    + g.Sum(x => x.Quantity).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                return g.Select(x => new ProcurementViewEntry(x.PositionId, title));
            }).ToList();
    }
}
