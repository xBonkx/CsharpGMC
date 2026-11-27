namespace GmcSmeta.Core.Db;

/// <summary>
/// Grouping and ordering for the cost view. A group keeps member IDs so the
/// same action can be applied to every original row, without editing an aggregate.
/// No SQL, WPF or hidden mutation happens in this projection.
/// </summary>
public static class CostSummaryGrouping
{
    public sealed record Group(string Key, string Label, IReadOnlyList<CostSummaryRow> Rows,
        bool IsSystem, bool IsEmpty)
    {
        public IReadOnlyList<long> PositionIds => Rows.Select(r => r.Id).Distinct().ToList();
        public decimal MaterialPurchase => Rows.Sum(r => r.MCost);
        public decimal MaterialRetail => Rows.Sum(r => r.MSell);
        public decimal WorkPurchase => Rows.Sum(r => r.WCost);
        public decimal WorkRetail => Rows.Sum(r => r.WSell);
    }

    public static readonly string[] Modes = { "kp", "same", "sup", "sup_same" };
    public static readonly string[] Sorts =
        { "kp", "name", "cost_up", "cost_down", "costsum", "markup", "color" };

    private static readonly string[] Palette =
        { "#FFF1A8", "#D4F0C8", "#F9D0D0", "#D2E6FF",
          "#FFDDB3", "#E6DAFA", "#E3E6EA" };

    private static int ColorSortOrder(string color)
    {
        var i = Array.FindIndex(Palette,
            p => string.Equals(p, color, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? Palette.Length : i;
    }

    private static IEnumerable<CostSummaryRow> Sort(IEnumerable<CostSummaryRow> rows, string sort) =>
        sort switch
        {
            "name" => rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            "cost_up" => rows.OrderBy(r => r.UCost is null ? 1 : 0)
                .ThenBy(r => r.UCost ?? 0m)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            "cost_down" => rows.OrderBy(r => r.UCost is null ? 1 : 0)
                .ThenByDescending(r => r.UCost ?? 0m)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            "costsum" => rows.OrderBy(r => r.UCost is null ? 1 : 0)
                .ThenByDescending(r => (r.UCost ?? 0m) * r.Qty),
            // Python cost_view._sort_key_now: markup asc; unpriced goes last.
            "markup" => rows.OrderBy(r => r.UCost is > 0m && r.UPrice != 0m ? 0 : 1)
                .ThenBy(r => r.UCost is > 0m && r.UPrice != 0m
                    ? r.UPrice / r.UCost.Value - 1m : 0m),
            "color" => rows.OrderBy(r => ColorSortOrder(r.CostColor)),
            _ => rows,
        };

    public static IReadOnlyList<Group> Build(CostSummaryData data, string mode, string sort)
    {
        if (!Modes.Contains(mode)) throw new ArgumentException("Unknown grouping.", nameof(mode));
        if (!Sorts.Contains(sort)) throw new ArgumentException("Unknown sorting.", nameof(sort));
        var rows = Sort(data.Rows, sort).ToArray();
        var result = new List<Group>();
        if (mode == "kp")
        {
            // Index once after sorting, preserving member order and empty
            // systems. Avoid scanning every position for every system.
            var bySystem = rows.ToLookup(r => r.SystemId);
            foreach (var sy in data.Systems)
            {
                var members = bySystem[sy.Id].ToArray();
                result.Add(new Group("sys:" + sy.Id, sy.Label, members, true, members.Length == 0));
            }
            return result;
        }

        if (mode == "same")
        {
            foreach (var g in rows.GroupBy(r => (r.Name, r.Unit)))
                result.Add(new Group("same:" + g.Key.Name + "|" + g.Key.Unit,
                    g.Key.Name + " · " + g.Key.Unit, g.ToArray(), false, false));
            return result;
        }

        foreach (var supplier in rows.GroupBy(r => r.Supplier ?? ""))
        {
            var supplierName = supplier.Key.Length == 0 ? "Без поставщика" : supplier.Key;
            if (mode == "sup")
            {
                result.Add(new Group("sup:" + supplier.Key, supplierName,
                    supplier.ToArray(), false, false));
                continue;
            }
            foreach (var g in supplier.GroupBy(r => (r.Name, r.Unit)))
                result.Add(new Group("sup_same:" + supplier.Key + "|" + g.Key.Name + "|" + g.Key.Unit,
                    supplierName + " · " + g.Key.Name + " · " + g.Key.Unit,
                    g.ToArray(), false, false));
        }
        return result;
    }
}
