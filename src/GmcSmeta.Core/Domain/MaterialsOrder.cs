using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>Frozen display order of top material rows. Owns only the sort
/// snapshot, not UI controls, prices, persistence or the SQL revision.</summary>
public sealed class MaterialsOrder
{
    private readonly Dictionary<long, MatSortKey> _materialsFrozenKeys = new();
    private (int ContractId, string Mode, string Sort)? _materialsFrozenDoc;

    public void Reset()
    {
        _materialsFrozenKeys.Clear();
        _materialsFrozenDoc = null;
    }

    private readonly record struct MatSortKey(int NullRank, decimal Num1, decimal Num2, string Text, int OrigIdx)
        : IComparable<MatSortKey>
    {
        public int CompareTo(MatSortKey other)
        {
            if (NullRank != other.NullRank) return NullRank.CompareTo(other.NullRank);
            if (Num1 != other.Num1) return Num1.CompareTo(other.Num1);
            if (Num2 != other.Num2) return Num2.CompareTo(other.Num2);
            var byText = string.Compare(Text, other.Text, StringComparison.CurrentCultureIgnoreCase);
            if (byText != 0) return byText;
            return OrigIdx.CompareTo(other.OrigIdx);
        }
    }

    private static MatSortKey MaterialsSortKey(CostMaterialsPosition p, int origIdx, string sort,
        IReadOnlyList<(string Name, string Hex)> colors)
    {
        var cu = p.UnitCost;
        var mk = cu is decimal unit && p.Price != 0m && unit != 0m ? (p.Price / unit - 1m) * 100m : (decimal?)null;
        return sort switch
        {
            "name" => new MatSortKey(0, 0m, 0m, p.Name, origIdx),
            "cost_up" => new MatSortKey(cu is null ? 1 : 0, cu ?? 0m, 0m, p.Name, origIdx),
            "cost_down" => new MatSortKey(cu is null ? 1 : 0, -(cu ?? 0m), 0m, p.Name, origIdx),
            "costsum" => new MatSortKey(cu is null ? 1 : 0, -(cu ?? 0m) * p.Qty, 0m, "", origIdx),
            "markup" => new MatSortKey(mk is null ? 1 : 0, mk ?? 0m, 0m, "", origIdx),
            "color" => new MatSortKey(0, MatColorRank(p.CostColor, colors), 0m, "", origIdx),
            _ => new MatSortKey(0, 0m, 0m, "", origIdx),
        };
    }

    private static decimal MatColorRank(string color, IReadOnlyList<(string Name, string Hex)> colors)
    {
        for (var i = 0; i < colors.Count; i++)
            if (string.Equals(colors[i].Hex, color, StringComparison.OrdinalIgnoreCase))
                return i;
        return colors.Count;
    }

    /// <summary>Keep edited rows at their original sort rank until an explicit
    /// view choice or document/mode change; new rows receive new keys.</summary>
    public List<CostMaterialsPosition> Order(int contractId, string mode, string sort,
        IReadOnlyList<CostMaterialsPosition> top, IReadOnlyList<(string Name, string Hex)> colors)
    {
        var doc = (contractId, mode, sort);
        if (_materialsFrozenDoc != doc)
        {
            _materialsFrozenKeys.Clear();
            _materialsFrozenDoc = doc;
        }
        if (sort == "kp" && mode == "kp")
            return top.ToList();
        var indexed = top.Select((p, i) => (P: p, Idx: i)).ToList();
        var keys = new Dictionary<long, MatSortKey>();
        foreach (var (p, idx) in indexed)
            keys[p.Id] = _materialsFrozenKeys.TryGetValue(p.Id, out var frozen)
                ? frozen : _materialsFrozenKeys[p.Id] = MaterialsSortKey(p, idx, sort, colors);
        return indexed.OrderBy(x => keys[x.P.Id]).Select(x => x.P).ToList();
    }
}
